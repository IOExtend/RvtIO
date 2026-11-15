using System.Text;
using RvtIO.Internal;
using RvtIO.Lookup;
using RvtIO.Model;
using static RvtIO.Parsing.StringScanner;

namespace RvtIO.Parsing;

internal static class BlockParser
{
    private static readonly string? DumpGrepEnvironment = Environment.GetEnvironmentVariable("RVTIO_DUMP_GREP");

    /// <summary>A GRep block collected for decoding after the walk of one or several chunks.</summary>
    internal readonly record struct PendingGRep(byte[] Chunk, uint Eid, int Start, uint Size, bool IsFamilyCut);

    /// <summary>Decodes the collected GRep blocks in parallel and applies them in collection order (the merge
    /// rules depend on arrival order).</summary>
    internal static void ApplyPendingGReps(ParsingState state, List<PendingGRep> pending)
    {
        if (pending.Count == 0) return;
        var decoded = new Internal.GRepData?[pending.Count];
        Parallel.For(0, decoded.Length, new ParallelOptions(), index =>
            decoded[index] = GRepParserV2.Parse(pending[index].Chunk, pending[index].Start, pending[index].Size));
        for (int index = 0; index < decoded.Length; index++)
        {
            var job = pending[index];
            if (job.IsFamilyCut)
            {
                if (!state.FamilyCutGreps.ContainsKey(job.Eid)
                    && decoded[index] is { } familyCut && IsAuthoredFamilyComplexCutter(familyCut))
                    state.FamilyCutGreps[job.Eid] = familyCut;
            }
            else ApplyGRepBlock(state, job.Eid, decoded[index]);
            decoded[index] = null;
        }
    }

    /// <summary>The largest block data the scan of a chunk reads (a larger block is not parsed); the geometry of a framed record over it is not
    /// decoded.</summary>
    internal const uint MaxBlockSize = 1_000_000;

    public static bool ParseChunk(byte[] chunk, ParsingState state, bool collectBlockOnlyIds = false,
        List<PendingGRep>? deferred = null)
    {
        // Block format: [elemId:4][hash:4][dataSize:4][classIdx:4][data: dataSize bytes]
        int pos = 0;
        int blockCount = 0;
        // GRep decoding is a pure function of the block bytes: the blocks are collected in stored order, decoded in
        // parallel after the walk and applied in that same order (the merge rules depend on arrival order).
        var pendingAll = deferred ?? new List<PendingGRep>();
        while (pos + 16 <= chunk.Length)
        {
            uint eid = BitConverter.ToUInt32(chunk, pos);

            // Skip R22/R20 records (format: [elemId:4][bodySize:4][0x053C:2][subclass:2][body])
            if (pos + 12 <= chunk.Length)
            {
                uint rBodySize = BitConverter.ToUInt32(chunk, pos + 4);
                ushort rMagic = BitConverter.ToUInt16(chunk, pos + 8);
                if ((rMagic == 0x053C || rMagic == 0x04F8)
                    && ElementIdEncoding.IsSupported(eid) && rBodySize > 0 && rBodySize < 500_000
                    && pos + 12 + (int)rBodySize <= chunk.Length)
                {
                    pos += 12 + (int)rBodySize;
                    continue;
                }
            }
            // Skip R25 records (format: [elemId:4][zero:4][hash:4][bodySize:4][body][bodySize:4])
            if (pos + 20 <= chunk.Length && ElementIdEncoding.IsSupported(eid))
            {
                uint rZero = BitConverter.ToUInt32(chunk, pos + 4);
                uint rBodySize = BitConverter.ToUInt32(chunk, pos + 12);
                if (rZero == 0 && rBodySize > 2 && rBodySize < 500_000
                    && pos + 16 + (int)rBodySize + 4 <= chunk.Length
                    && BitConverter.ToUInt32(chunk, pos + 16 + (int)rBodySize) == rBodySize)
                {
                    pos += 16 + (int)rBodySize + 4;
                    continue;
                }
            }

            uint dataSize = BitConverter.ToUInt32(chunk, pos + 8);

            // Skip blocks with implausible dataSize (check before bounds to avoid int overflow)
            if (dataSize > MaxBlockSize) { pos++; continue; }
            // Skip zero-elemId entries (padding/terminators) in 4-byte strides
            if (eid == 0) { pos += 4; continue; }
            if (dataSize == 0) { pos++; continue; }
            // Hard stop if block would exceed chunk (format broken at this point)
            if (pos + 16 + (int)dataSize > chunk.Length)
            {
                if (state.StaleCopiesRemoved && !state.AllKnownIds.Contains(eid)) { pos++; continue; }
                break;
            }

            // A block owner is authoritative when it is present in ElemTable or when the
            // block's on-disk footer repeats dataSize. This exact format signature rejects
            // byte-aligned false positives in family-document regions.
            bool knownOwner = state.AllKnownIds.Contains(eid);
            bool hasSizeFooter = dataSize >= 4
                && BitConverter.ToUInt32(chunk, pos + 16 + (int)dataSize - 4) == dataSize;
            if (!knownOwner && !hasSizeFooter) { pos++; continue; }

            blockCount++;
            if (!ParseBlock(chunk, pos, eid, dataSize, knownOwner, state, collectBlockOnlyIds, pendingAll)) { pos++; continue; }
            pos += 16 + (int)dataSize;
        }
        if (deferred == null) ApplyPendingGReps(state, pendingAll);
        return blockCount > 0;
    }

    /// <summary>Reads one block [elemId:4][hash:4][dataSize:4][classIdx:4][data] whose header the caller accepted: the block inventory, the
    /// category of its owner and every fact the block parser takes from a block. False when the class is 0 (no block).</summary>
    internal static bool ParseBlock(byte[] chunk, int pos, uint eid, uint dataSize, bool knownOwner, ParsingState state,
        bool collectBlockOnlyIds, List<PendingGRep> pendingAll)
    {
        int dataStart = pos + 16;
        int dataEnd = dataStart + (int)dataSize;
        uint classIdx = BitConverter.ToUInt32(chunk, pos + 12);

        if (!knownOwner)
        {
            if (collectBlockOnlyIds)
                state.BlockOnlyIds.Add(eid);
        }

        // Collect block classIdx inventory (low 16 bits = class type, high 16 varies per block)
        uint classKey = classIdx & 0xFFFF;
        // classKey=0 is never a valid class - these are false positives from scanner misalignment
        if (classKey == 0) return false;
        if (ElementIdEncoding.IsSupported(eid))
        {
            if (!state.BlockClassInventory.TryGetValue(classKey, out var bcs))
                state.BlockClassInventory[classKey] = bcs = new BlockClassStats();
            bcs.Count++;
            bcs.TotalBytes += dataSize;
            if (dataSize < bcs.MinSize) bcs.MinSize = (int)dataSize;
            if (dataSize > bcs.MaxSize) bcs.MaxSize = (int)dataSize;
            if (bcs.SampleElemIds.Count < 5) bcs.SampleElemIds.Add(eid);
            if (bcs.SampleData == null)
            {
                int sampleLen = Math.Min(32, (int)dataSize);
                bcs.SampleData = new byte[sampleLen];
                Array.Copy(chunk, dataStart, bcs.SampleData, 0, sampleLen);
            }
            if (state.ElemTable.TryGetValue(eid, out uint typeId) && typeId != 0)
            {
                bcs.TypeIdCounts.TryGetValue(typeId, out int tc);
                bcs.TypeIdCounts[typeId] = tc + 1;
            }
        }

        // Assign the category from the Tf classIdx mapping (if the element has none yet):
        // first the id registry, then the R22 def table (id -> class name)
        // joined with the name -> category map.
        ushort tfClassId = (ushort)(classIdx & 0xFFFF);
        if (tfClassId > 0 && state.Records.TryGetValue(eid, out var recForCat) && !recForCat.CategoryId.HasValue)
        {
            if (TfClassRegistry.R22TfClassIdToCategory.TryGetValue(tfClassId, out int catId))
                recForCat.CategoryId = catId;
            // NOTE: an automatic fallback via R22ClassDefs.Names -> NameToCategory is not used:
            // early categorization changes downstream pass behavior and needs per-row validation first.
        }

        if (tfClassId == 480 && !state.BasePointCoordinates.ContainsKey(eid))
        {
            var coordinates = TfBlockDeserializer.ExtractBasePointCoordinates(
                chunk, pos, dataSize, tfClassId);
            if (coordinates is { } bp) state.BasePointCoordinates[eid] = bp;
        }

        // Project Level blocks are small and may live in structurally recovered chunks
        // that the continuous anchored walk cannot enter. Decode them directly here so
        // DatumPlane.m_text and m_isBuildingStory are not lost.
        if (tfClassId == 2299)
        {
            var np = TfBlockDeserializer.ExtractParams(chunk, pos, dataSize, tfClassId)
                ?? new TfBlockDeserializer.NativeParams();
            np.Fields["__isLevel"] = 1;
            var levelName = TfBlockDeserializer.ExtractLevelName(
                chunk, pos, dataSize, tfClassId);
            if (!string.IsNullOrWhiteSpace(levelName))
                np.StringFields["m_text"] = levelName;
            if (TfBlockDeserializer.ExtractLevelElevation(
                chunk, pos, dataSize, tfClassId) is double levelElevation)
                np.DoubleFields["__levelElevation"] = levelElevation;
            state.NativeParams[eid] = np;
        }

        // An image symbol's own block (class 0x089A) holds the pixel size of the image: two u32 at data+0x80.
        if ((classIdx & 0xFFFF) == 0x089A && dataSize >= 0x88 && !state.ImageSizes.ContainsKey(eid))
        {
            int width = BitConverter.ToInt32(chunk, dataStart + 0x80);
            int height = BitConverter.ToInt32(chunk, dataStart + 0x84);
            if (width is > 0 and < 20000 && height is > 0 and < 20000)
                state.ImageSizes[eid] = (width, height);
        }

        // A viewer's own block (class 0x05BF) holds the basis of the view it belongs to; the chunk-level collector
        // below finds those blocks, which the walk of the project's own block does not always reach.

        // A viewport's own block (class 0x07C6) holds the view it displays and the offset of that view on the sheet:
        // m_offset = three doubles at payload+0x47, m_dbViewId = a u32 at payload+0x8F.
        if ((classIdx & 0xFFFF) == 0x07C6 && dataSize >= 0x93 && !state.ViewportPlacements.ContainsKey(eid))
            ExtractViewport(chunk, dataStart, eid, state);

        // A text note's own block holds its text and position. The note can be an element the block walk discovered
        // after the element table, so the block is read here and not under the known-id guard below.
        if ((classIdx & 0xFFFF) == 0x0F83 && !state.TextNotes.ContainsKey(eid))
            ExtractTextNote(chunk, dataStart, dataEnd, eid, state);

        if (state.AllKnownIds.Contains(eid))
        {
            ExtractBlockStrings(chunk, dataStart, dataEnd, eid, state.ElemStrings);

            ExtractBlockDoubles(chunk, dataStart, dataEnd, eid, state.ElemDoubles);

            // Blocks with high classIdx word = 0xFFFF contain BIP key->double pairs at stride 12
            if ((classIdx >> 16) == 0xFFFF)
            {
                ExtractBipDoublesFromBlock(chunk, dataStart, dataEnd, eid, state.ElemBipDoubles, state.AllKnownIds);
                ExtractBaseFieldsFromBlock(chunk, dataStart, dataEnd, eid, state.ElemBaseFields);
                ExtractWallIntBipsFromBlock(chunk, dataStart, dataEnd, eid, state.ElemBipInts, state.Records);
                ExtractHostObjBipsFromBlock(chunk, dataStart, dataEnd, eid, state.ElemBipInts);
                ExtractCurveDriverFromBlock(chunk, dataStart, dataEnd, eid, state.CurveDriverCandidates, state.Records);
                // Only extract names from proper Tf-serialized SymbolInfo blocks (not render material blocks = 0xFFFFFFFF)
                if (classIdx != 0xFFFFFFFF)
                    ExtractNameFromBlock(chunk, dataStart, dataEnd, eid, state.NativeElementNames);
            }

            // classIdx=0x702 blocks contain user param values (strings + doubles) and BIP ElementId values
            if ((classIdx & 0xFFFF) == 0x0702)
            {
                ExtractParamValuesFrom0702Block(chunk, dataStart, dataEnd, eid, state.AllKnownIds, state.ElemParamStrings, state.ElemBipDoubles);
                ExtractBipIntsFrom0702Block(chunk, dataStart, dataEnd, eid, state.AllKnownIds, state.ElemBipInts, state.Records);
                ExtractFamilyInstanceTransform(chunk, dataStart, dataEnd, eid, state.FamilyInstanceTransforms, state.Records);
                ExtractInsertSillFrom0702Block(chunk, dataStart, dataEnd, eid, state.ElemBipDoubles);
            }

            // Dimension / constraint reference geometry. Two block families share the
            // same payload layout ([self elemId @0x24][null refs][FCFFFFFF marker]
            // [u32 point count @marker+7][reference-point (x,y,z) f64 triples @marker+11]):
            //   0x091F - dimensions (OST_Dimensions / OST_WeakDims / OST_Constraints)
            //   0x009D - constraints (OST_Constraints; coincident points = alignment/lock)
            if (((classIdx & 0xFFFF) == 0x091F || (classIdx & 0xFFFF) == 0x009D)
                && !state.Dimensions.ContainsKey(eid))
                ExtractDimensionData(chunk, dataStart, dataEnd, eid, state.Dimensions);

            // classIdx=0x0346 blocks: OST_SketchLines - the 2D profile curves of sketches.
            // The curve is stored in the same GInfoBlob GLine/GArc form as wall location
            // curves (Flags 0x01080004), decoded into a LocationCurve.
            if ((classIdx & 0xFFFF) == 0x0346)
            {
                state.CurveBlockOwners.Add(eid);
                if (!state.SketchCurves.ContainsKey(eid))
                    ExtractSketchCurve(chunk, dataStart, dataEnd, eid, state.SketchCurves, state.DetailCurveStyles);
            }

            // Native parameter stores are extracted by the continuous-stream walker
            // (TfStreamWalker in RvtFileReader) - blocks cross chunk boundaries, so the
            // per-chunk walk here sees only ~8% of them.

            // classIdx=0x09B6 blocks: OST_Materials - the material's shading appearance
            // (name + simple-graphics color, shininess, transparency, smoothness).
            if ((classIdx & 0xFFFF) == 0x09B6 && !state.MaterialAppearances.ContainsKey(eid))
                ExtractMaterialAppearance(chunk, dataStart, dataEnd, eid, state.MaterialAppearances, state.MaterialFinishes);

            // HVAC load energy defaults (heat gains, lighting/power loads, air changes, set
            // points) at fixed offsets. 0x085A = HVACLoadSpaceType, 0x0857 = HVACLoadBuildingType
            // (same field layout).
            if (((classIdx & 0xFFFF) == 0x085A || (classIdx & 0xFFFF) == 0x0857)
                && !state.HvacSpaceTypeParams.ContainsKey(eid))
                ExtractHvacSpaceType(chunk, dataStart, dataEnd, eid,
                    state.HvacSpaceTypeParams, state.HvacScheduleRefs);

            // classIdx=0x0884 blocks: analytical model Hub nodes - own-block signature
            // used to categorize the (otherwise uncategorized) node as OST_AnalyticalNodes.
            if ((classIdx & 0xFFFF) == 0x0884)
                state.AnalyticalNodeOwners.Add(eid);

            // classIdx=0x03E3 blocks: view/sheet info. For ViewSheet elements the block
            // carries [8 zero bytes][len-prefixed sheet number][len-prefixed issue date].
            if ((classIdx & 0xFFFF) == 0x03E3 && !state.SheetInfo.ContainsKey(eid))
                ExtractSheetInfo(chunk, dataStart, dataEnd, eid, state.SheetInfo);

            // View display name: name-field signature in the element's own block.
            if (!state.ViewNames.ContainsKey(eid))
                ExtractViewName(chunk, dataStart, dataEnd, eid, state.ViewNames);

            // classIdx=0x0E17 blocks (R22): Level class-specific data.
            // Offset 0xF4 contains the level elevation as a double (in feet).
            if ((classIdx & 0xFFFF) == 0x0E17 && dataSize >= 0xFC)
            {
                double elev = BitConverter.ToDouble(chunk, dataStart + 0xF4);
                if (!double.IsNaN(elev) && !double.IsInfinity(elev) && Math.Abs(elev) < 100_000)
                {
                    if (!state.ElemBipDoubles.TryGetValue(eid, out var bipMap))
                        state.ElemBipDoubles[eid] = bipMap = new Dictionary<int, double>();
                    bipMap[-1007102] = elev; // LEVEL_ELEV
                }
            }

            // classIdx=0x00012100 blocks: CompoundStructure data for wall types.
            // Offset 0x18 contains the wall function enum (0=Interior..5=CoreShaft).
            if (classIdx == 0x00012100 && dataSize >= 0x1C)
            {
                int funcVal = BitConverter.ToInt32(chunk, dataStart + 0x18);
                if (funcVal >= 0 && funcVal <= 5)
                    state.WallTypeFunctions[eid] = funcVal;
            }

            // 0xFFFF0200 / 0xFFFF1026 blocks: CompoundStructure with layer data.
            // Scan for marker 0x1003 followed by layer count + 29-byte layer entries.
            if ((classIdx >> 16) == 0xFFFF && dataSize > 100 && !state.CompoundLayers.ContainsKey(eid))
            {
                ExtractCompoundLayers(chunk, dataStart, dataEnd, eid, state.CompoundLayers);
            }

            // classIdx=0x0FBD blocks: ForgeTypeId Spec Registry - unit format definitions
            if ((classIdx & 0xFFFF) == 0x0FBD && state.ForgeSpecRegistry.Count == 0)
            {
                ExtractForgeSpecRegistry(chunk, dataStart, dataEnd, state.ForgeSpecRegistry);
            }

            // classIdx=0x0B3A blocks: Family Parameter Definitions - ForgeId -> spec type mapping
            if ((classIdx & 0xFFFF) == 0x0B3A)
            {
                ExtractForgeParamSpec(chunk, dataStart, dataEnd, state.ForgeParamSpecs);
            }

            // 0x0725 blocks: FamilyType/Symbol definitions - extract element name
            // The first valid string after the Tf header (~offset 400+) is the type name
            if ((classIdx & 0xFFFF) == 0x0725 && dataSize > 400 && !state.NativeElementNames.ContainsKey(eid))
            {
                ExtractNameFrom0725Block(chunk, dataStart, dataEnd, eid, state.NativeElementNames);
            }

            // 0x0725 blocks of family symbols ALSO contain type-level param value entries
            // (after the chain table) shaped as `[type:4][marker:u16=1][zeros:8][double:8]
            // [FFFFFFFF][paramKey:4]`. These are family-doc defaults that apply to all
            // instances unless overridden by the instance's 0x0702 block.
            if ((classIdx & 0xFFFF) == 0x0725 && dataSize > 400)
            {
                ExtractFamilySymbolDoubles(chunk, dataStart, dataEnd, eid, state.FamilySymbolBlockDoubles);
                ExtractFamilySymbolStrings(chunk, dataStart, dataEnd, eid, state.FamilySymbolBlockStrings);
                ExtractFamilySymbolInts(chunk, dataStart, dataEnd, eid, state.FamilySymbolBlockInts);
                ExtractFamilySymbolElemIdPairs(chunk, dataStart, dataEnd, eid, state.FamilySymbolBlockElemIds);

                if (!state.FamilyCutGreps.ContainsKey(eid))
                    pendingAll.Add(new PendingGRep(chunk, eid, dataStart - 16, dataSize, true));
            }

            // 0x0725 blocks also contain the family param mapping table.
            // Each entry is 30 bytes: [FFFFFFFF:4][binKey:4][zeros:4][marker:u32 at offset 12][14 bytes].
            // Entries with marker == 1 form a linked list: bin paramKey of entry N maps to
            // bin paramKey of entry N+1 (the actual project param def ID).
            if ((classIdx & 0xFFFF) == 0x0725 && dataSize > 400 && !state.FamilyParamMaps.ContainsKey(eid))
            {
                var (map, typeValues) = ExtractFamilyParamMapAndValues(chunk, dataStart, dataEnd);
                if (map.Count > 0)
                    state.FamilyParamMaps[eid] = map;
                if (typeValues.Count > 0)
                    state.FamilyTypeValues[eid] = typeValues;
            }

            // GRep blocks (classIdx & 0xFFFF == 0x07D7 R22, 0x07D6 R20): geometry representation
            // Some elements have multiple GRep blocks - prefer the one with world-coordinate
            // face directions (non-axis-aligned XVectors indicate world coordinates).
            if ((classIdx & 0xFFFF) is 0x07D7 or 0x07D6)
            {
                // Diagnostic: RVTIO_DUMP_GREP=<eid> dumps the raw GRep block.
                if (DumpGrepEnvironment is { } dge
                    && dge.Split(',').Any(x => uint.TryParse(x, out uint dgeId) && dgeId == eid))
                {
                    try
                    {
                        int hdr = dataStart - 16;
                        int dlen = Math.Min(dataEnd - hdr, chunk.Length - hdr);
                        var dbuf = new byte[dlen];
                        Array.Copy(chunk, hdr, dbuf, 0, dlen);
                        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), $"grep7d7_{eid}_{classIdx:X8}_{hdr}.bin"), dbuf);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        DiagnosticSink.WriteLine($"Block dump failed: {ex.Message}");
                    }
                }
                // Production geometry is decoded only through the declared Tf schema:
                // a pattern scan cannot confirm field ownership.
                pendingAll.Add(new PendingGRep(chunk, eid, dataStart - 16, dataSize, false));
            }
        }

        return true;
    }

    /// <summary>Merges one decoded GRep block into the element's geometry (arrival-order rules).</summary>
    private static void ApplyGRepBlock(ParsingState state, uint eid, Internal.GRepData? gdV2)
    {
                    
                    // Edge UV pairs are mapped to world via the face Planes (scanned from
                    // the block tail): a real tessellated BRep. Use it whenever it produced
                    // >=2 world faces; else keep the pattern scan.
                    var gd = gdV2;
                    // GInstance reference (family instances): carry the symbol id + placement
                    // regardless of which decoder supplied the faces - the element's real
                    // geometry is the SYMBOL's, transformed (IfcMappedItem architecture).
                    if (gdV2 is { Instances.Count: > 0 })
                    {
                        gd ??= new Internal.GRepData();
                        MergeGRepInstances(gd, gdV2);
                    }
                    if (gd != null)
                    {
                        if (!state.GrepBlocks.TryGetValue(eid, out var existing))
                        {
                            state.GrepBlocks[eid] = gd;
                        }
                        else
                        {
                            // Separate GRep blocks may store the resolved display mesh beside an
                            // explicit GInstance mapped representation. They are alternatives, not
                            // additive geometry: combining their faces duplicates all or part of
                            // the symbol body. The serialized GInstance is authoritative because it
                            // identifies both the source symbol and its exact placement transform.
                            bool? preferCandidateMapped = PreferExplicitMappedRepresentation(existing, gd);
                            if (preferCandidateMapped.HasValue)
                            {
                                if (preferCandidateMapped.Value) state.GrepBlocks[eid] = gd;
                                return;
                            }

                            // Tiebreakers (in priority order):
                            //   1. Upgrade if existing has no faces and new one does
                            //   2. Prefer world-coordinate transform
                            //   3. Prefer non-axis-aligned faces (world-oriented)
                            // Don't naively pick "more faces" - different blocks describe different
                            // aspects (e.g. an analytical model vs the actual wall) and the larger
                            // block may not be the structural geometry.
                            bool replace = false;
                            int VertCount(Internal.GRepData g) =>
                                g.Faces.Sum(fc => fc.Vertices?.Count ?? 0);
                            int gv = VertCount(gd), ev = VertCount(existing);
                            // Real tessellated geometry (more actual polygon vertices) always wins.
                            if (gv > ev + 2) replace = true;
                            else if (existing.Faces.Count == 0 && gd.Faces.Count > 0) replace = true;
                            else if (gd.Transform != null && existing.Transform == null) replace = true;
                            else if (HasNonAxisAlignedFaces(gd) && !HasNonAxisAlignedFaces(existing)) replace = true;
                            if (replace)
                            {
                                MergeGRepInstances(gd, existing);
                                state.GrepBlocks[eid] = gd;
                            }
                            else MergeGRepInstances(existing, gd);
                        }
                    }
    }

    /// <summary>A partial family cutter needs its serialized dummy-host closure plane.
    /// A complete cutter does not, but is accepted only when it belongs to the exact
    /// FamilySymbol.m_complexCutGeoms branch and owns a serialized closed solid.</summary>
    internal static bool IsAuthoredFamilyComplexCutter(GRepData geometry)
    {
        if (!geometry.IsFamilyComplexCut || geometry.Faces.Count == 0) return false;
        bool hasClosurePlane = geometry.FamilyClosurePlaneOrigin is { Length: 3 }
            && geometry.FamilyClosurePlaneNormal is { Length: 3 };
        return hasClosurePlane || geometry.Solids.Any(solid => solid.IsClosed);
    }

    /// <summary>
    /// Returns which of two separate GRep blocks to keep when exactly one is an explicit
    /// mapped representation. A null result means both blocks have the same representation
    /// kind and the ordinary geometry-quality comparison must decide between them.
    /// </summary>
    internal static bool? PreferExplicitMappedRepresentation(GRepData existing, GRepData candidate)
    {
        bool existingMapped = existing.Instances.Count > 0;
        bool candidateMapped = candidate.Instances.Count > 0;
        return existingMapped == candidateMapped ? null : candidateMapped;
    }

    private static void MergeGRepInstances(GRepData target, GRepData source)
    {
        foreach (var instance in source.Instances)
            if (!target.Instances.Any(x => x.SymbolElemId == instance.SymbolElemId
                && x.Transform.SequenceEqual(instance.Transform)))
                target.Instances.Add(new GRepInstance
                {
                    SymbolElemId = instance.SymbolElemId,
                    Transform = (double[])instance.Transform.Clone(),
                });
        var primary = target.Instances.FirstOrDefault();
        if (primary != null)
        {
            target.SymbolElemId = primary.SymbolElemId;
            target.InstanceTransform = primary.Transform;
        }
    }

    /// <summary>
    /// Check if any face in the GRep data has non-axis-aligned direction vectors,
    /// which indicates the geometry is in world coordinates (not wall-local).
    /// </summary>
    private static bool HasNonAxisAlignedFaces(GRepData gd)
    {
        foreach (var face in gd.Faces)
        {
            double ax = Math.Abs(face.XVector[0]), ay = Math.Abs(face.XVector[1]);
            if (ax > 0.01 && ay > 0.01)
                return true;
        }
        return false;
    }

    public static void ExtractBlockStrings(byte[] d, int start, int end, uint eid, Dictionary<uint, List<string>> elemStrings)
    {
        for (int j = start; j + 6 <= end; j++)
        {
            uint sLen = BitConverter.ToUInt32(d, j);
            if (sLen < 2 || sLen > 500 || j + 4 + sLen * 2 > end) continue;

            bool valid = true;
            int printable = 0;
            for (int k = 0; k < (int)sLen && valid; k++)
            {
                byte lo = d[j + 4 + k * 2], hi = d[j + 4 + k * 2 + 1];
                // Accept: ASCII (hi=0x00), Cyrillic (hi=0x04), known Deflate64 artifacts (hi=0xF7/0x1B),
                // or any hi when lo is printable ASCII (handles Deflate64 hi-byte corruption in Cyrillic+ASCII strings)
                if (hi != 0x00 && hi != 0x04 && hi != 0xF7 && hi != 0x1B && (lo < 0x20 || lo > 0x7E))
                { valid = false; break; }
                char c = DecodeUtf16Char(lo, hi);
                if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') { valid = false; break; }
                if (char.IsLetterOrDigit(c) || char.IsPunctuation(c) || char.IsSymbol(c)) printable++;
            }
            if (!valid || printable < sLen * 0.6) continue;

            var sb = new StringBuilder();
            for (int k = 0; k < (int)sLen; k++)
            {
                char c = DecodeUtf16Char(d[j + 4 + k * 2], d[j + 4 + k * 2 + 1]);
                if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') { sb.Clear(); break; }
                sb.Append(c);
            }
            string str = sb.ToString().Trim();
            if (str.Length < 2) continue;
            if (!elemStrings.ContainsKey(eid)) elemStrings[eid] = new List<string>();
            if (!elemStrings[eid].Contains(str) && elemStrings[eid].Count < 20)
                elemStrings[eid].Add(str);
        }
    }

    /// <summary>
    /// Extract the stored sill height of a hosted insert (window/door) from its 0x0702 block.
    /// The block body is a Tf class chain; the 0x08BE segment holds the insert placement:
    /// `[FFFFFFFF][classId:u16=0x08BE][00 x4][sillHeight:double][?:double][hostId:u32]`.
    /// </summary>
    private static void ExtractInsertSillFrom0702Block(byte[] d, int start, int end, uint eid,
        Dictionary<uint, Dictionary<int, double>> elemBipDoubles)
    {
        for (int i = start; i + 30 <= end; i++)
        {
            if (d[i] != 0xFF || d[i + 1] != 0xFF || d[i + 2] != 0xFF || d[i + 3] != 0xFF) continue;
            if (d[i + 4] != 0xBE || d[i + 5] != 0x08) continue;
            if (d[i + 6] != 0 || d[i + 7] != 0 || d[i + 8] != 0 || d[i + 9] != 0) continue;
            double sill = BitConverter.ToDouble(d, i + 10);
            if (double.IsNaN(sill) || double.IsInfinity(sill) || Math.Abs(sill) > 100_000) continue;
            if (!elemBipDoubles.TryGetValue(eid, out var map))
                elemBipDoubles[eid] = map = new Dictionary<int, double>();
            if (!map.ContainsKey(-1001361))
                map[-1001361] = sill; // INSTANCE_SILL_HEIGHT_PARAM
            return;
        }
    }

    // Extract base element fields from 0xFFFF blocks using elemId as anchor.
    // Layout after elemId: [AssocLevelId:4][FamId:4][UnplacedOwnerId:4][OwnerDBViewId:4]
    //                      [CreatedPhaseId:4][DemolishedPhaseId:4][BaseDesignOptionId:4]
    private static void ExtractBaseFieldsFromBlock(byte[] d, int start, int end, uint eid,
        Dictionary<uint, BaseElementFields> baseFields)
    {
        if (baseFields.ContainsKey(eid)) return;
        for (int i = start; i + 32 <= end; i += 2)
        {
            if (BitConverter.ToUInt32(d, i) != eid) continue;
            int lvl = BitConverter.ToInt32(d, i + 4);
            int fam = BitConverter.ToInt32(d, i + 8);
            int un = BitConverter.ToInt32(d, i + 12);
            int ow = BitConverter.ToInt32(d, i + 16);
            // Validate: UnplacedOwnerId and OwnerDBViewId are almost always -1
            if (un != -1 || ow != -1) continue;
            if (lvl != -1 && (lvl <= 0 || lvl >= 1_000_000)) continue;
            baseFields[eid] = new BaseElementFields
            {
                AssocLevelId = lvl,
                FamId = fam,
                CreatedPhaseId = BitConverter.ToInt32(d, i + 20),
                DemolishedPhaseId = BitConverter.ToInt32(d, i + 24),
                BaseDesignOptionId = BitConverter.ToInt32(d, i + 28)
            };
            return;
        }
    }

    // Extract wall-specific integer BIP values from 0xFFFF blocks.
    // Uses Tf-serialized field layout: after the anchor pattern
    // [WallAttrId:4][UpToLevelId:4][AnalSurfId:4][AnalTop:4][AnalBot:4][ContFooting:4]
    // the integer BIP fields are at fixed offsets:
    // +24: MullionTdFlags, +28: Version, +32: SubWallGridIdInType,
    // +36: WallStructuralUsage, +40: WallKeyRef, +44: WallCrossSection,
    // +48: IsFlipped(bool), +49: IsStructuralSignificant(bool)
    private static void ExtractWallIntBipsFromBlock(byte[] d, int start, int end, uint eid,
        Dictionary<uint, Dictionary<int, int>> elemBipInts,
        Dictionary<uint, ElementRecord> records)
    {
        if (!records.TryGetValue(eid, out var rec) || rec.CategoryId != -2000011) return;

        // Search for the anchor pattern:
        // [positive elemId:4][positive elemId or -1:4][-9:4][-12:4][-12:4][-1:4]
        // This is WallAttrId, UpToLevelId, AnalyticalProjectionSurfaceId(-9), AnalyticalTopPlane(-12), AnalyticalBottomPlane(-12), ContFootingId(-1)
        for (int i = start; i + 52 <= end; i += 2)
        {
            int wallAttrId = BitConverter.ToInt32(d, i);
            int upToLevelId = BitConverter.ToInt32(d, i + 4);
            int analSurf = BitConverter.ToInt32(d, i + 8);
            int analTop = BitConverter.ToInt32(d, i + 12);
            int analBot = BitConverter.ToInt32(d, i + 16);
            int contFooting = BitConverter.ToInt32(d, i + 20);

            // Validate anchor: WallAttrId must be a positive element ID,
            // UpToLevelId positive or -1, analytical fields small negatives or -1, ContFooting = -1
            if (wallAttrId <= 0) continue;
            if (upToLevelId == 0) continue;
            // Analytical fields: either -9/-12/-12 (with analytical model) or -1/-1/-1 (without)
            bool analPattern1 = analSurf == -9 && (analTop >= -100 && analTop <= 0) && (analBot >= -100 && analBot <= 0);
            bool analPattern2 = analSurf == -1 && analTop == -1 && analBot == -1;
            if (!analPattern1 && !analPattern2) continue;
            if (contFooting != -1) continue;

            // VWall integer BIP fields are at fixed offsets from the anchor
            int baseOff = i;
            if (baseOff + 50 > end) continue; // need 50 bytes from anchor start

            int version = BitConverter.ToInt32(d, baseOff + 28);
            int subWallGridId = BitConverter.ToInt32(d, baseOff + 32);
            int wallStructUsage = BitConverter.ToInt32(d, baseOff + 36);
            int wallKeyRef = BitConverter.ToInt32(d, baseOff + 40);
            int wallCrossSection = BitConverter.ToInt32(d, baseOff + 44);
            bool isFlipped = d[baseOff + 48] != 0;
            bool isStructSignificant = d[baseOff + 49] != 0;

            if (wallStructUsage < 0 || wallStructUsage > 10) continue;
            if (wallKeyRef < -1 || wallKeyRef > 20) continue;
            if (wallCrossSection < 0 || wallCrossSection > 10) continue;

            if (!elemBipInts.TryGetValue(eid, out var dict))
                elemBipInts[eid] = dict = new Dictionary<int, int>();

            // WALL_STRUCTURAL_USAGE_PARAM = -1001119
            dict[-1001119] = wallStructUsage;
            // WALL_KEY_REF_PARAM = -1001122
            dict[-1001122] = wallKeyRef;
            // WALL_CROSS_SECTION = -1019100
            dict[-1019100] = wallCrossSection;
            // WALL_STRUCTURAL_SIGNIFICANT = -1001596
            dict[-1001596] = isStructSignificant ? 1 : 0;

            return;
        }
    }

    // Extract m_roomBounding from HostObj section of 0xFFFF blocks.
    // HostObj fields: m_hostObjMiscData (ref), m_oGeomToElemMap (ref), m_roomBounding (bool)
    // When both refs are null, the pattern is:
    //   [FFFFFFFF:4][0826:2][FFFFFFFF:4][0827:2][roomBounding:1]
    // Class tags: 0x0826 = HostObjMiscData, 0x0827 = GeomToElemMap
    private static void ExtractHostObjBipsFromBlock(byte[] d, int start, int end, uint eid,
        Dictionary<uint, Dictionary<int, int>> elemBipInts)
    {
        // ROOM_BOUNDING BIP = -1001007
        const int BIP_ROOM_BOUNDING = -1001007;

        if (elemBipInts.TryGetValue(eid, out var existing) && existing.ContainsKey(BIP_ROOM_BOUNDING))
            return;

        // Scan for the null-ref pattern: FFFFFFFF 2608 FFFFFFFF 2708
        for (int i = start; i + 13 <= end; i++)
        {
            if (BitConverter.ToUInt32(d, i) != 0xFFFFFFFF) continue;
            if (BitConverter.ToUInt16(d, i + 4) != 0x0826) continue;
            if (i + 12 > end) break;
            if (BitConverter.ToUInt32(d, i + 6) != 0xFFFFFFFF) continue;
            if (BitConverter.ToUInt16(d, i + 10) != 0x0827) continue;

            // m_roomBounding is the byte right after the two null refs
            byte roomBounding = d[i + 12];
            if (roomBounding > 1) continue;

            if (!elemBipInts.TryGetValue(eid, out var dict))
                elemBipInts[eid] = dict = new Dictionary<int, int>();
            dict[BIP_ROOM_BOUNDING] = roomBounding;
            return;
        }
    }

    /// <summary>
    /// Extract FamilyInstance transform (Trf) by scanning for a 3x4 matrix pattern.
    /// Trf: 12 doubles column-major [Col0(3d), Col1(3d), Col2(3d), Translation(3d)].
    /// After matrix: SymbolId(i32, positive, valid element ID).
    /// Validation: rotation columns must be orthonormal unit vectors.
    /// </summary>
    public static void ExtractFamilyInstanceTransform(byte[] d, int start, int end, uint eid,
        Dictionary<uint, FamilyInstanceData> familyInstanceTransforms,
        Dictionary<uint, ElementRecord>? records = null)
    {
        if (familyInstanceTransforms.ContainsKey(eid)) return;

        const int TRF_BYTES = 96; // 12 doubles
        if (end - start < TRF_BYTES + 4) return;

        // Scan backwards from end - InstanceInfo is near end of record body.
        // Use 2-byte alignment (variable-length fields can cause misalignment).
        int lastStart = end - TRF_BYTES - 4;
        lastStart -= (lastStart - start) % 2;
        for (int i = lastStart; i >= start; i -= 2)
        {
            // Quick pre-check: first double of Col0 should be a reasonable rotation value [-1..1]
            double r00 = BitConverter.ToDouble(d, i);
            if (double.IsNaN(r00) || Math.Abs(r00) > 1.01) continue;

            var trf = new double[12];
            trf[0] = r00;
            bool valid = true;
            for (int k = 1; k < 12; k++)
            {
                trf[k] = BitConverter.ToDouble(d, i + k * 8);
                if (double.IsNaN(trf[k])) { valid = false; break; }
            }
            if (!valid) continue;

            bool colsOk = true;
            for (int col = 0; col < 3; col++)
            {
                double x = trf[col * 3], y = trf[col * 3 + 1], z = trf[col * 3 + 2];
                double len2 = x * x + y * y + z * z;
                if (len2 < 0.81 || len2 > 1.21) { colsOk = false; break; } // ~ 0.9^2 to 1.1^2
            }
            if (!colsOk) continue;

            double dot01 = trf[0] * trf[3] + trf[1] * trf[4] + trf[2] * trf[5];
            double dot02 = trf[0] * trf[6] + trf[1] * trf[7] + trf[2] * trf[8];
            double dot12 = trf[3] * trf[6] + trf[4] * trf[7] + trf[5] * trf[8];
            if (Math.Abs(dot01) > 0.01 || Math.Abs(dot02) > 0.01 || Math.Abs(dot12) > 0.01) continue;

            double tx = trf[9], ty = trf[10], tz = trf[11];
            if (Math.Abs(tx) > 1e6 || Math.Abs(ty) > 1e6 || Math.Abs(tz) > 1e6) continue;

            // Try two layouts:
            // Layout A (column-major 3x4): SymbolId is int32 right after 12 doubles (96 bytes)
            // Layout B (row-major 4x4): 4th row [0,0,0,1] after 12 doubles, then SymbolId after 16 doubles (128 bytes)
            int symbolId = -1;
            double[] finalTrf = trf;

            // Check Layout B first: see if doubles 12-15 are [0, 0, 0, 1]
            if (i + 128 + 4 <= end)
            {
                double r30 = BitConverter.ToDouble(d, i + 96);
                double r31 = BitConverter.ToDouble(d, i + 104);
                double r32 = BitConverter.ToDouble(d, i + 112);
                double r33 = BitConverter.ToDouble(d, i + 120);
                if (Math.Abs(r30) < 1e-10 && Math.Abs(r31) < 1e-10 && Math.Abs(r32) < 1e-10 && Math.Abs(r33 - 1.0) < 1e-10)
                {
                    // Row-major 4x4: [r00,r01,r02,tx, r10,r11,r12,ty, r20,r21,r22,tz, 0,0,0,1]
                    // Convert to column-major 3x4: [r00,r10,r20, r01,r11,r21, r02,r12,r22, tx,ty,tz]
                    finalTrf = new double[]
                    {
                        trf[0], trf[4], trf[8],   // Col0
                        trf[1], trf[5], trf[9],   // Col1
                        trf[2], trf[6], trf[10],  // Col2
                        trf[3], trf[7], trf[11],  // Translation
                    };
                    int sid = BitConverter.ToInt32(d, i + 128);
                    if (sid > 0)
                        symbolId = sid;
                }
            }

            if (symbolId <= 0 && i + TRF_BYTES + 4 <= end)
            {
                int sid = BitConverter.ToInt32(d, i + TRF_BYTES);
                if (sid > 0)
                {
                    symbolId = sid;
                    finalTrf = trf;
                }
            }

            if (symbolId <= 0) continue;

            // Validate: SymbolId should be in the element's reference list (if available)
            if (records != null && records.TryGetValue(eid, out var rec) && rec.References.Count > 0)
            {
                bool found = false;
                foreach (var r in rec.References)
                    if (r == (uint)symbolId) { found = true; break; }
                if (!found) continue;
            }

            familyInstanceTransforms[eid] = new FamilyInstanceData
            {
                Trf = finalTrf,
                SymbolId = symbolId,
            };
            return;
        }
    }

    // Extract CurveDriver (wall location curve) from 0xFFFF blocks.
    // The VWallDriver contains a GLine or GArc curve with:
    //   GInfoBlob (16 bytes): [Tag:4][ControlCommand:4=0][GStyleElemId:4=-1][Flags:4=0x01080004]
    //   Tag=0 -> GLine, Tag=1 -> GArc
    //   EndParams: [doublex2] (no count field - always exactly 2 for wall curves)
    //   GLine: OriginPoint(3d) + DirectionVector(3d)
    //   GArc:  XDirection(3d) + YDirection(3d) + Radius(double) + Center(3d) + IsFilled(bool)
    internal static void ExtractCurveDriverFromBlock(byte[] d, int start, int end, uint eid,
        Dictionary<uint, List<CurveDriverData>> curveDriverCandidates,
        Dictionary<uint, ElementRecord> records)
    {
        if (!records.TryGetValue(eid, out var rec) || rec.CategoryId != -2000011) return;

        // Search for GInfoBlob pattern: Tag=0|1, CtrlCmd=0, GStyleElemId=-1, Flags=0x01080004
        for (int i = start; i + 80 <= end; i++)
        {
            int tag = BitConverter.ToInt32(d, i);
            if (tag != 0 && tag != 1) continue;
            if (BitConverter.ToInt32(d, i + 4) != 0) continue;
            if (BitConverter.ToUInt32(d, i + 8) != 0xFFFFFFFF) continue;

            uint flags = BitConverter.ToUInt32(d, i + 12);
            if (flags != 0x01080004) continue;

            bool isLine = tag == 0;
            bool isArc = tag == 1;

            // EndParams: 2 doubles directly after GInfoBlob (no count field)
            int pos = i + 16;
            if (pos + 16 > end) continue;
            double ep0 = BitConverter.ToDouble(d, pos);
            double ep1 = BitConverter.ToDouble(d, pos + 8);
            pos += 16;

            if (double.IsNaN(ep0) || double.IsNaN(ep1)) continue;
            if (Math.Abs(ep0) > 1e6 || Math.Abs(ep1) > 1e6) continue;
            if (ep1 <= ep0) continue;

            var cd = new CurveDriverData { EndParams = [ep0, ep1] };

            if (isLine)
            {
                if (pos + 48 > end) continue;
                double ox = BitConverter.ToDouble(d, pos);
                double oy = BitConverter.ToDouble(d, pos + 8);
                double oz = BitConverter.ToDouble(d, pos + 16);
                double dx = BitConverter.ToDouble(d, pos + 24);
                double dy = BitConverter.ToDouble(d, pos + 32);
                double dz = BitConverter.ToDouble(d, pos + 40);

                if (double.IsNaN(ox) || double.IsNaN(oy) || double.IsNaN(oz)) continue;
                if (Math.Abs(ox) > 1e6 || Math.Abs(oy) > 1e6 || Math.Abs(oz) > 1e6) continue;
                double dirLen = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (dirLen < 0.9 || dirLen > 1.1) continue;

                cd.IsArc = false;
                cd.OriginPoint = [ox, oy, oz];
                cd.DirectionVector = [dx, dy, dz];
            }
            else
            {
                // XDirection: 3 doubles, YDirection: 3 doubles, Radius: 1 double, Center: 3 doubles, IsFilled: 1 byte
                if (pos + 81 > end) continue;
                double xdx = BitConverter.ToDouble(d, pos);
                double xdy = BitConverter.ToDouble(d, pos + 8);
                double xdz = BitConverter.ToDouble(d, pos + 16);
                double ydx = BitConverter.ToDouble(d, pos + 24);
                double ydy = BitConverter.ToDouble(d, pos + 32);
                double ydz = BitConverter.ToDouble(d, pos + 40);
                double radius = BitConverter.ToDouble(d, pos + 48);
                double cx = BitConverter.ToDouble(d, pos + 56);
                double cy = BitConverter.ToDouble(d, pos + 64);
                double cz = BitConverter.ToDouble(d, pos + 72);

                double xdLen = Math.Sqrt(xdx * xdx + xdy * xdy + xdz * xdz);
                double ydLen = Math.Sqrt(ydx * ydx + ydy * ydy + ydz * ydz);
                if (xdLen < 0.9 || xdLen > 1.1) continue;
                if (ydLen < 0.9 || ydLen > 1.1) continue;
                if (double.IsNaN(radius) || radius < 0.01 || radius > 1e6) continue;
                if (double.IsNaN(cx) || Math.Abs(cx) > 1e6) continue;
                if (double.IsNaN(cy) || Math.Abs(cy) > 1e6) continue;
                if (double.IsNaN(cz) || Math.Abs(cz) > 1e6) continue;

                cd.IsArc = true;
                cd.XDirection = [xdx, xdy, xdz];
                cd.YDirection = [ydx, ydy, ydz];
                cd.Radius = radius;
                cd.CenterPoint = [cx, cy, cz];
                // For arc, OriginPoint = Center, DirectionVector = XDirection (for compatibility)
                cd.OriginPoint = [cx, cy, cz];
                cd.DirectionVector = [xdx, xdy, xdz];
            }

            if (!curveDriverCandidates.TryGetValue(eid, out var list))
                curveDriverCandidates[eid] = list = new List<CurveDriverData>();
            list.Add(cd);
        }
    }

    // Extract the element name from 0x0725 FamilyType blocks.
    // The first non-autodesk string after the Tf header region (~400 bytes) is the type name.
    private static void ExtractNameFrom0725Block(byte[] d, int start, int end, uint eid,
        Dictionary<uint, string> nativeNames)
    {
        if (nativeNames.ContainsKey(eid)) return;
        // Skip Tf header: first ~400 bytes are class hierarchy fields
        int scanStart = start + Math.Min(400, (end - start) / 3);
        for (int j = scanStart; j + 6 <= end; j++)
        {
            uint sLen = BitConverter.ToUInt32(d, j);
            if (sLen < 2 || sLen > 200 || j + 4 + (int)sLen * 2 > end) continue;
            bool valid = true;
            int printable = 0;
            for (int k = 0; k < (int)sLen && valid; k++)
            {
                byte lo = d[j + 4 + k * 2], hi = d[j + 4 + k * 2 + 1];
                if (hi != 0x00 && hi != 0x04 && hi != 0xF7 && hi != 0x1B && (lo < 0x20 || lo > 0x7E))
                { valid = false; break; }
                char c = StringScanner.DecodeUtf16Char(lo, hi);
                if (c < 0x20 && c != '\t') { valid = false; break; }
                if (char.IsLetterOrDigit(c) || char.IsPunctuation(c) || char.IsSymbol(c)) printable++;
            }
            if (!valid || printable < sLen * 0.6) continue;
            var sb = new System.Text.StringBuilder();
            for (int k = 0; k < (int)sLen; k++)
                sb.Append(StringScanner.DecodeUtf16Char(d[j + 4 + k * 2], d[j + 4 + k * 2 + 1]));
            string str = sb.ToString().Trim();
            if (str.StartsWith("autodesk.")) continue; // skip ForgeTypeId strings
            if (!StringScanner.IsValidElementName(str)) continue;
            nativeNames[eid] = str;
            return;
        }
    }

    /// <summary>Scan a 0xFFFF block for family-symbol type-level param value entries.
    /// Pattern: `[type=4:u32][marker=1:u16][zeros:8][double:8][FFFFFFFF:4][paramKey:4]`.
    /// A relaxed scan is used: find every `FFFFFFFF` followed by a plausible paramKey, then
    /// read the 8 bytes just before the sentinel as a double. Garbage is filtered out by requiring
    /// the double to be a finite real number in a sensible range.</summary>
    private static void ExtractFamilySymbolDoubles(byte[] d, int start, int end, uint eid,
        Dictionary<uint, Dictionary<uint, double>> resultByEid)
    {
        for (int i = start; i + 8 <= end; i++)
        {
            if (d[i] != 0xFF || d[i+1] != 0xFF || d[i+2] != 0xFF || d[i+3] != 0xFF) continue;
            uint paramKey = BitConverter.ToUInt32(d, i + 4);
            // Plausible project param def ID range
            if (paramKey < 200 || paramKey > int.MaxValue) continue;
            // BIP (negative) values would have all 4 high bytes 0xFF - exclude
            if ((paramKey & 0xFF000000) == 0xFF000000) continue;
            if (i < start + 8) continue;
            double dv = BitConverter.ToDouble(d, i - 8);
            if (double.IsNaN(dv) || double.IsInfinity(dv)) continue;
            double ad = Math.Abs(dv);
            if (ad != 0 && (ad < 1e-12 || ad > 1e9)) continue;
            // Skip values that look like packed int data - high u32 must be a reasonable double sign/exponent
            uint hi = BitConverter.ToUInt32(d, i - 4);
            // Reject if hi bytes look like an unrelated int (e.g. another element id)
            if (ad == 0)
            {
                // For zero values, require explicit "all zeros" preceding 8 bytes
                bool allZero = true;
                for (int k = 0; k < 8; k++) if (d[i-8+k] != 0) { allZero = false; break; }
                if (!allZero) continue;
            }

            if (!resultByEid.TryGetValue(eid, out var map))
                resultByEid[eid] = map = new Dictionary<uint, double>();
            // Prefer first occurrence per (eid, paramKey)
            if (!map.ContainsKey(paramKey)) map[paramKey] = dv;
        }
    }

    /// <summary>Scan a 0x0725 family-symbol block for ElementId-valued type-level params.
    /// Pattern in a family symbol block:
    ///   `[zeros>=8][elemId:4][projKey:4][zeros>=12][FFFFFFFF][marker:4]`
    /// where elemId is a real project element (Material, FamilyType, Level) and projKey is
    /// the project param def ID that should receive that ElementId as its value.</summary>
    private static void ExtractFamilySymbolElemIdPairs(byte[] d, int start, int end, uint eid,
        Dictionary<uint, Dictionary<int, int>> resultByEid)
    {
        // Walk 4-byte aligned positions; check for two consecutive uint32s, each plausibly
        // a project elem-id, preceded by >=8 zero bytes and followed by >=12 zero bytes + FFFFFFFF.
        for (int i = start + 8; i + 24 <= end; i += 2)
        {
            bool zerosBefore = true;
            for (int z = 1; z <= 8; z++) if (d[i - z] != 0) { zerosBefore = false; break; }
            if (!zerosBefore) continue;

            int v1 = BitConverter.ToInt32(d, i);
            int v2 = BitConverter.ToInt32(d, i + 4);
            if (v1 < 200) continue;
            if (v2 < 200) continue;
            if (v2 < 100_000) continue;

            bool zerosAfter = true;
            for (int z = 8; z < 16; z++) if (d[i + z] != 0) { zerosAfter = false; break; }
            if (!zerosAfter) continue;
            bool foundSentinel = false;
            for (int z = 16; z < 28 && i + z + 4 <= end; z++)
            {
                if (d[i + z] == 0xFF && d[i + z + 1] == 0xFF && d[i + z + 2] == 0xFF && d[i + z + 3] == 0xFF)
                { foundSentinel = true; break; }
            }
            if (!foundSentinel) continue;

            if (!resultByEid.TryGetValue(eid, out var map))
                resultByEid[eid] = map = new Dictionary<int, int>();
            if (!map.ContainsKey(v2)) map[v2] = v1;
        }
    }

    /// <summary>Scan a 0x0725 family-symbol block for type-level INTEGER parameter entries.
    /// Pattern: `[FFFFFFFF][paramKey:i32][value:i32][marker:u32=1][padding][FFFFFFFF][typeMarker:2]`.
    /// The value is the 4 bytes AFTER the param key (unlike doubles where value is BEFORE
    /// the sentinel). The next FFFFFFFF marks the end of the entry.</summary>
    private static void ExtractFamilySymbolInts(byte[] d, int start, int end, uint eid,
        Dictionary<uint, Dictionary<int, int>> resultByEid)
    {
        for (int i = start; i + 18 <= end; i++)
        {
            if (d[i] != 0xFF || d[i+1] != 0xFF || d[i+2] != 0xFF || d[i+3] != 0xFF) continue;
            int paramKey = BitConverter.ToInt32(d, i + 4);
            // Require LARGER threshold than for strings/doubles to avoid matching the inner sentinel
            // of string entries (where i+4 is the "typeMarker" like 0x0EB6 = 3766).
            bool isPositive = paramKey > 100_000;
            bool isBip = paramKey > -2_000_000 && paramKey < -100;
            if (!isPositive && !isBip) continue;

            // Value at i+8, marker u32 = 1 at i+12. Verify marker.
            uint marker = BitConverter.ToUInt32(d, i + 12);
            if (marker != 1) continue;

            int value = BitConverter.ToInt32(d, i + 8);
            if (Math.Abs(value) > 100_000_000) continue;
            if (d[i+16] != 0 || d[i+17] != 0) continue;

            if (!resultByEid.TryGetValue(eid, out var map))
                resultByEid[eid] = map = new Dictionary<int, int>();
            if (!map.ContainsKey(paramKey)) map[paramKey] = value;
        }
    }

    /// <summary>Scan a 0x0725 family-symbol block for type-level string parameter entries.
    /// Pattern of R22 family params:
    ///   `[slen:u32][string:slen*2 UTF-16LE][FFFFFFFF][typeMarker:4 = 0x0EB6][zeros:6+][FFFFFFFF][paramKey:4]`
    /// The string is followed by an inner sentinel + type-marker (0x0EB6 for strings) and trailing
    /// zeros before the outer FFFFFFFF + paramKey. We accept the outer sentinel and walk back through
    /// the typeMarker region to find the slen-prefixed string.</summary>
    private static void ExtractFamilySymbolStrings(byte[] d, int start, int end, uint eid,
        Dictionary<uint, Dictionary<int, string>> resultByEid)
    {
        for (int i = start; i + 8 <= end; i++)
        {
            if (d[i] != 0xFF || d[i+1] != 0xFF || d[i+2] != 0xFF || d[i+3] != 0xFF) continue;
            int paramKey = BitConverter.ToInt32(d, i + 4);
            // Plausible param key: positive user param or BIP (-1000000..-1200000 range)
            bool isPositive = paramKey > 200;
            bool isBip = paramKey > -2_000_000 && paramKey < -100;
            if (!isPositive && !isBip) continue;

            // Walk back looking for the inner FFFFFFFF that separates the string from the outer
            // sentinel. Search within 30 bytes back (typical type-marker + zeros = 14 bytes).
            int innerSentinel = -1;
            for (int b = Math.Max(start, i - 30); b + 4 <= i; b++)
            {
                if (d[b] == 0xFF && d[b+1] == 0xFF && d[b+2] == 0xFF && d[b+3] == 0xFF)
                { innerSentinel = b; break; }
            }
            if (innerSentinel < 0) continue;
            int strEnd = innerSentinel;
            // Try slen values; require slen u32 at (strEnd - slen*2 - 4)
            for (int slen = 1; slen <= 300; slen++)
            {
                int strStart = strEnd - slen * 2;
                int slenPos = strStart - 4;
                if (slenPos < start) break;
                if (BitConverter.ToUInt32(d, slenPos) != (uint)slen) continue;
                bool valid = true;
                for (int k = 0; k < slen && valid; k++)
                {
                    byte lo = d[strStart + k * 2], hi = d[strStart + k * 2 + 1];
                    if (hi != 0x00 && hi != 0x04 && hi != 0xF7 && hi != 0x1B && (lo < 0x20 || lo > 0x7E))
                    { valid = false; break; }
                    char c = DecodeUtf16Char(lo, hi);
                    if (c < 0x20 && c != '\t' && c != '\r' && c != '\n') { valid = false; break; }
                }
                if (!valid) continue;
                // Reject empty strings; not useful as type defaults
                if (slen == 0) continue;

                var sb = new StringBuilder();
                for (int k = 0; k < slen; k++)
                    sb.Append(DecodeUtf16Char(d[strStart + k * 2], d[strStart + k * 2 + 1]));
                string s = sb.ToString();
                if (!resultByEid.TryGetValue(eid, out var map))
                    resultByEid[eid] = map = new Dictionary<int, string>();
                if (!map.ContainsKey(paramKey)) map[paramKey] = s;
                break;
            }
        }
    }

    /// <summary>Extract the family parameter mapping table AND type-level values from a 0x0725
    /// (FamilyType) block.
    ///
    /// The block contains chains of entries laid out as:
    ///   [FFFFFFFF:4][binKey:4][zeros:4][marker:u16 at +12][slen:u16 at +14][padding to 18]
    ///   [string:slen*2 bytes if slen>0]
    ///   trailing bytes - may contain a trailing 8-byte double right before the next sentinel
    /// Each entry is normally 30 bytes when slen=0; with a string it's larger.
    /// Entries with marker == 1 are user parameter slots.
    ///
    /// Returns:
    ///   * map: binKey -> projParamId (next-entry chain rule)
    ///   * typeValues: projParamId -> string or double (the entry's payload, if any)</summary>
    private static (Dictionary<uint, uint> map, Dictionary<uint, object> typeValues)
        ExtractFamilyParamMapAndValues(byte[] d, int start, int end)
    {
        var map = new Dictionary<uint, uint>();
        var typeValues = new Dictionary<uint, object>();
        var allEntries = new List<(int offset, int entryLen, uint binKey, ushort marker, ushort slen, string? str)>();

        int pos = start;
        while (pos + 18 <= end)
        {
            if (BitConverter.ToUInt32(d, pos) != 0xFFFFFFFF) { pos++; continue; }
            // Validate basic shape: 4 zeros at +8-11
            bool zerosOk = true;
            for (int z = 0; z < 4 && zerosOk; z++) if (d[pos + 8 + z] != 0) zerosOk = false;
            if (!zerosOk) { pos++; continue; }
            int chainStart = pos;
            int chainStartIndex = allEntries.Count;
            int chainLen = 0;
            while (pos + 18 <= end && BitConverter.ToUInt32(d, pos) == 0xFFFFFFFF)
            {
                uint binKey = BitConverter.ToUInt32(d, pos + 4);
                bool zo = true;
                for (int z = 0; z < 4 && zo; z++) if (d[pos + 8 + z] != 0) zo = false;
                if (!zo) break;
                ushort marker = BitConverter.ToUInt16(d, pos + 12);
                ushort slen = BitConverter.ToUInt16(d, pos + 14);
                int strBytes = slen > 0 && slen < 2000 && pos + 18 + slen * 2 <= end ? slen * 2 : 0;
                string? str = null;
                if (strBytes > 0)
                {
                    var sb = new StringBuilder();
                    bool valid = true;
                    for (int k = 0; k < slen; k++)
                    {
                        byte lo = d[pos + 18 + k * 2], hi = d[pos + 18 + k * 2 + 1];
                        if (hi != 0x00 && hi != 0x04 && hi != 0xF7 && hi != 0x1B && (lo < 0x20 || lo > 0x7E))
                        { valid = false; break; }
                        sb.Append(DecodeUtf16Char(lo, hi));
                    }
                    if (valid) str = sb.ToString();
                }
                // Entry length: at least 30 bytes (slen=0 case), else 18 + strBytes rounded up to 30 minimum
                int entryLen = Math.Max(30, 18 + strBytes);
                allEntries.Add((pos, entryLen, binKey, marker, slen, str));
                pos += entryLen;
                chainLen++;
                while (pos < end && d[pos] == 0) pos++;
            }
            if (chainLen < 3)
            {
                // False-positive chain - roll back
                allEntries.RemoveRange(chainStartIndex, chainLen);
                pos = chainStart + 1;
            }
        }

        var filtered = allEntries.Where(e => e.marker == 1).ToList();
        for (int i = 0; i < filtered.Count - 1; i++)
        {
            uint binKey = filtered[i].binKey;
            uint projKey = filtered[i + 1].binKey;
            if (binKey > 0 && projKey > 0
                && !map.ContainsKey(binKey))
            {
                map[binKey] = projKey;
            }

            // The value is associated with the entry's own binKey, which itself maps via the chain to the project key;
            // the chain rule for 0x725 values differs from 0x702. Here the value is stored under binKey and
            // resolved to projKey when propagating to instances.
            object? value = null;
            if (!string.IsNullOrEmpty(filtered[i].str))
                value = filtered[i].str!;
            else
            {
                int nextOff = (i + 1 < filtered.Count) ? filtered[i + 1].offset : -1;
                if (nextOff >= 8)
                {
                    double dv = BitConverter.ToDouble(d, nextOff - 8);
                    if (dv != 0 && !double.IsNaN(dv) && !double.IsInfinity(dv)
                        && Math.Abs(dv) < 1e12 && Math.Abs(dv) > 1e-20)
                        value = dv;
                }
            }
            if (value != null && !typeValues.ContainsKey(binKey))
                typeValues[binKey] = value;
        }
        return (map, typeValues);
    }

    /// <summary>Returns the last structurally valid length-prefixed element name in one
    /// block payload. Callers must retain the owning block's provenance separately; the
    /// aggregate ElemStrings collection cannot confirm which block supplied a string.</summary>
    internal static string? ExtractTerminalElementName(
        byte[] data, int start, int end)
    {
        string? terminalName = null;
        for (int offset = start; offset + 6 <= end; offset++)
        {
            if (TryReadElementName(
                data, offset, end, out string? name,
                allowHostObjectTypeName: true))
                terminalName = name;
        }
        return terminalName;
    }

    // In the block data, scan for the first string that passes IsValidElementName.
    private static void ExtractNameFromBlock(byte[] d, int start, int end, uint eid,
        Dictionary<uint, string> nativeNames)
    {
        if (nativeNames.ContainsKey(eid)) return;
        for (int j = start; j + 6 <= end; j++)
        {
            if (!TryReadElementName(d, j, end, out string? name)) continue;
            nativeNames[eid] = name;
            return;
        }
    }

    private static bool TryReadElementName(
        byte[] data, int offset, int end, out string name,
        bool allowHostObjectTypeName = false)
    {
        name = string.Empty;
        uint stringLength = BitConverter.ToUInt32(data, offset);
        if (stringLength < 3 || stringLength > 300
            || offset + 4L + stringLength * 2L > end)
            return false;

        bool valid = true;
        int printable = 0;
        for (int index = 0; index < (int)stringLength && valid; index++)
        {
            byte low = data[offset + 4 + index * 2];
            byte high = data[offset + 4 + index * 2 + 1];
            if (high != 0x00 && high != 0x04
                && high != 0xF7 && high != 0x1B
                && (low < 0x20 || low > 0x7E))
            {
                valid = false;
                break;
            }
            char character = StringScanner.DecodeUtf16Char(low, high);
            if (character < 0x20 && character != '\t')
            {
                valid = false;
                break;
            }
            if (char.IsLetterOrDigit(character)
                || char.IsPunctuation(character)
                || char.IsSymbol(character))
                printable++;
        }
        if (!valid || printable < stringLength * 0.6)
            return false;

        var builder = new System.Text.StringBuilder((int)stringLength);
        for (int index = 0; index < (int)stringLength; index++)
            builder.Append(StringScanner.DecodeUtf16Char(
                data[offset + 4 + index * 2],
                data[offset + 4 + index * 2 + 1]));
        string candidate = builder.ToString().Trim();
        if (!(allowHostObjectTypeName
                ? StringScanner.IsValidHostObjectTypeName(candidate)
                : StringScanner.IsValidElementName(candidate)))
            return false;
        name = candidate;
        return true;
    }

    // Extract key->double pairs from blocks with classIdx high word = 0xFFFF.
    // Format: [double:8][key:i32] at stride 12 -- value PRECEDES the key.
    // Starting at first known negative BIP key, read value from 8 bytes before each key.
    private static void ExtractBipDoublesFromBlock(byte[] d, int start, int end, uint eid,
        Dictionary<uint, Dictionary<int, double>> bipDoubles, HashSet<uint>? knownIds = null)
    {
        for (int i = start; i + 4 <= end; i += 2) // scan at 2-byte alignment (Tf data may not be 4-aligned)
        {
            int key = BitConverter.ToInt32(d, i);
            // Valid BIP range: negative, not -1, within known BIP range
            if (key >= -1 || key < -2_000_000 || !BipHelpers.IsPossibleBipKey(key)) continue;

            if (!bipDoubles.TryGetValue(eid, out var dict))
                bipDoubles[eid] = dict = new Dictionary<int, double>();

            for (int p = i; p + 4 <= end; p += 12)
            {
                int k = BitConverter.ToInt32(d, p);
                if (k == 0 || k == -1) break;
                // Value is 8 bytes BEFORE the key
                if (p - 8 >= start)
                {
                    double v = BitConverter.ToDouble(d, p - 8);
                    if (!double.IsNaN(v) && !double.IsInfinity(v)
                        && (v == 0 || (Math.Abs(v) >= 1e-15 && Math.Abs(v) <= 1e9)))
                    {
                        if (k < 0 && BipHelpers.IsPossibleBipKey(k))
                            dict[k] = v;
                        else if (k > 100 && knownIds != null && knownIds.Contains((uint)k))
                            dict[k] = v; // user param double
                    }
                }
            }
            break;
        }
    }

    public static void ExtractBlockDoubles(byte[] d, int start, int end, uint eid, Dictionary<uint, List<double>> elemDoubles)
    {
        for (int j = start; j + 8 <= end; j += 4)
        {
            double dv = BitConverter.ToDouble(d, j);
            if (dv != 0 && !double.IsNaN(dv) && !double.IsInfinity(dv)
                && Math.Abs(dv) < 1e12 && Math.Abs(dv) > 1e-20)
            {
                if (!elemDoubles.ContainsKey(eid)) elemDoubles[eid] = new List<double>();
                if (!elemDoubles[eid].Contains(dv) && elemDoubles[eid].Count < 30)
                    elemDoubles[eid].Add(dv);
            }
        }
    }

    // 0x702 block: user param values (strings + doubles)
    // Mixed entry types, all preceded by [FF FF FF FF:4][paramDefId:4]:
    //   String: [FF FF FF FF:4][paramDefId:4][zeros:6][slen:2][zeros:2][string:slen*2 UTF-16LE]
    //   Double: [double:8][FF FF FF FF:4][paramDefId:4] (value BEFORE sentinel, 8 bytes back)
    //   Null:   [zeros:8][FF FF FF FF:4][paramDefId:4]
    private static void ExtractParamValuesFrom0702Block(byte[] d, int start, int end, uint eid,
        HashSet<uint> elemSet,
        Dictionary<uint, Dictionary<uint, string>> elemParamStrings,
        Dictionary<uint, Dictionary<int, double>> elemParamDoubles)
    {
        // Each entry is laid out as:
        //   [FFFFFFFF:4][binParamKey:4][zeros:4][marker:u16][slen:u16][padding][string:slen*2]
        // The 8 bytes just BEFORE an entry's sentinel are the trailing double value for THAT
        // entry (slen=0 case). I.e. the entry's "tail" doubles are stored at the boundary
        // overlapping with the previous entry's padding.
        for (int i = start; i + 18 <= end; i++)
        {
            if (BitConverter.ToUInt32(d, i) != 0xFFFFFFFF) continue;
            uint paramDefId = BitConverter.ToUInt32(d, i + 4);
            // Structural validation: paramKey followed by 6 zeros, then 2-byte slen, then padding.
            // (The 0x702 block does NOT carry the 0x725-style marker byte - these 6 bytes are zero.)
            if (paramDefId == 0 || paramDefId == 0xFFFFFFFF || paramDefId > 100_000_000) continue;
            bool headerValid = true;
            for (int z = 0; z < 6 && headerValid; z++) if (d[i + 8 + z] != 0) headerValid = false;
            if (!headerValid) continue;

            // Trailing double for THIS entry: 8 bytes just before the sentinel
            if (i >= start + 8)
            {
                double dv = BitConverter.ToDouble(d, i - 8);
                if (dv != 0 && !double.IsNaN(dv) && !double.IsInfinity(dv)
                    && Math.Abs(dv) < 1e12 && Math.Abs(dv) > 1e-20)
                {
                    if (!elemParamDoubles.TryGetValue(eid, out var dblDict))
                        elemParamDoubles[eid] = dblDict = new Dictionary<int, double>();
                    dblDict[(int)paramDefId] = dv;
                }
            }

            ushort slen = BitConverter.ToUInt16(d, i + 14);
            if (slen > 0 && slen < 2000 && i + 18 + slen * 2 <= end)
            {
                var sb = new StringBuilder();
                bool valid = true;
                for (int k = 0; k < slen; k++)
                {
                    byte lo = d[i + 18 + k * 2], hi = d[i + 18 + k * 2 + 1];
                    if (hi != 0x00 && hi != 0x04 && hi != 0xF7 && hi != 0x1B && (lo < 0x20 || lo > 0x7E))
                    { valid = false; break; }
                    sb.Append(DecodeUtf16Char(lo, hi));
                }
                if (valid && sb.Length > 0)
                {
                    if (!elemParamStrings.TryGetValue(eid, out var strDict))
                        elemParamStrings[eid] = strDict = new Dictionary<uint, string>();
                    strDict[paramDefId] = sb.ToString();
                }
                i += 17 + slen * 2;
                continue;
            }
            // slen=0 entries are NOT added here - Revit may interpret them as 0 (Integer/Bool/Double)
            // depending on the param def's spec, not "". Let PropagateChainPlaceholders add them
            // later with type inference (it has access to ForgeParamDefs by then).
            i += 7;
        }
    }

    // Extract BIP ElementId values from 0x702 block's BIP section.
    // The BIP section precedes user param entries. It contains sentinel-delimited entries.
    // Key patterns:
    //   - [FFFFFFFF][elemId:4][20+ xFF] = ELEM_TYPE_PARAM (unique long FF gap)
    //   - Earlier sentinels contain other ElementId BIP values in order matching body entries
    private static void ExtractBipIntsFrom0702Block(byte[] d, int start, int end, uint eid,
        HashSet<uint> elemSet,
        Dictionary<uint, Dictionary<int, int>> elemBipInts,
        Dictionary<uint, ElementRecord> records)
    {
        if (!records.TryGetValue(eid, out var rec)) return;
        var body = rec.Body;
        int mp = -1;
        for (int ii = 0; ii < body.Length - 1; ii++)
            if (BitConverter.ToUInt16(body, ii) == 0x0548) { mp = ii; break; }
        if (mp < 0) return;

        // Collect ElementId BIP keys from body entries (ft=4, negative key, ElementId storage type)
        var elementIdBips = new List<int>();
        for (int p = 2; p + 14 <= mp; p += 14)
        {
            int k = BitConverter.ToInt32(body, p + 4);
            ushort ft = BitConverter.ToUInt16(body, p + 8);
            if (ft != 4 || k >= -1 || k < -2_000_000) continue;
            if (!BipNames.Lookup.TryGetValue(k, out string? nm)) continue;
            if (BipHelpers.BipStorageType(nm) == StorageType.ElementId)
                elementIdBips.Add(k);
        }
        if (elementIdBips.Count == 0) return;

        int blockLen = end - start;
        if (!elemBipInts.TryGetValue(eid, out var intDict))
            elemBipInts[eid] = intDict = new Dictionary<int, int>();

        // Extract sentinel values from BIP section (scan until long FF gap = ELEM_TYPE_PARAM)
        // Sentinel pattern: [FFFFFFFF+][value:4] -- the value after each FF run
        var sentinelValues = new List<(int val, bool inElemSet)>();
        for (int i = 0; i < blockLen - 8; i++)
        {
            if (BitConverter.ToUInt32(d, start + i) != 0xFFFFFFFF) continue;
            int ffEnd = i + 4;
            while (ffEnd < blockLen && d[start + ffEnd] == 0xFF) ffEnd++;
            if (ffEnd + 4 > blockLen) break;
            uint val = BitConverter.ToUInt32(d, start + ffEnd);

            if (val > 0)
            {
                int afterVal = ffEnd + 4;
                int ffCount = 0;
                while (afterVal + ffCount < blockLen && d[start + afterVal + ffCount] == 0xFF)
                    ffCount++;
                if (ffCount >= 20 && elemSet.Contains(val))
                {
                    // Long FF gap after value -> ELEM_TYPE_PARAM
                    intDict[-1002050] = (int)val;
                    break;
                }
                sentinelValues.Add(((int)val, elemSet.Contains(val)));
            }
            i = ffEnd - 1;
        }

        // Map sentinel values to BIPs where we can identify them:
        // - FAMILY_LEVEL_PARAM: sentinel value that is a known Level element
        // - ELEM_FAMILY_AND_TYPE_PARAM: same as ELEM_TYPE_PARAM for most elements
        // - Other sentinels: store as-is if they're valid element IDs
        bool hasFamilyLevel = elementIdBips.Contains(-1001352);
        if (hasFamilyLevel)
        {
            // FAMILY_LEVEL_PARAM: among the sentinel values in elemSet, the one that is not a link/file reference
            // and has a small id (levels are typically created early) is taken as the level.
            foreach (var (sv, inSet) in sentinelValues)
            {
                if (!inSet) continue;
                if (sv < 10000 && !intDict.ContainsKey(-1001352))
                    intDict[-1001352] = sv;
            }
        }

        // ELEM_FAMILY_AND_TYPE_PARAM is typically same as ELEM_TYPE_PARAM
        if (intDict.ContainsKey(-1002050) && elementIdBips.Contains(-1002052))
            intDict[-1002052] = intDict[-1002050];
    }

    /// <summary>Extract ForgeId -> spec type mapping from 0x0B3A family parameter definition blocks.
    /// Each block contains a "autodesk.parameter.*" ForgeId and a "autodesk.spec.*" spec type.</summary>
    private static void ExtractForgeParamSpec(byte[] d, int start, int end, Dictionary<string, string> paramSpecs)
    {
        string? forgeId = null, specType = null;
        for (int j = start; j + 6 <= end; j++)
        {
            uint sLen = BitConverter.ToUInt32(d, j);
            if (sLen < 10 || sLen > 300 || j + 4 + (int)sLen * 2 > end) continue;
            bool valid = true;
            for (int k = 0; k < (int)sLen && valid; k++)
            {
                byte hi = d[j + 4 + k * 2 + 1];
                if (hi > 0x04 && hi != 0xF7) valid = false;
            }
            if (!valid) continue;
            string s = Encoding.Unicode.GetString(d, j + 4, (int)sLen * 2);
            if (s.StartsWith("autodesk.parameter."))
                forgeId = s;
            else if (s.StartsWith("autodesk.spec."))
                specType = s;
            if (forgeId != null && specType != null)
            {
                paramSpecs.TryAdd(forgeId, specType);
                break;
            }
            j += 3 + (int)sLen * 2;
        }
    }

    /// <summary>Extract ForgeTypeId Spec Registry from 0x0FBD blocks.
    /// Scans for consecutive UTF-16LE strings grouped as (spec, symbol, unit) triples
    /// with a conversion factor double after each unit string.</summary>
    private static void ExtractForgeSpecRegistry(byte[] d, int start, int end, Dictionary<string, ForgeSpecEntry> registry)
    {
        // Scan for length-prefixed UTF-16LE strings containing dots (ForgeTypeId URNs)
        var allStrings = new List<(int offset, string value)>();
        for (int j = start; j + 6 <= end; j++)
        {
            uint sLen = BitConverter.ToUInt32(d, j);
            if (sLen < 3 || sLen > 500 || j + 4 + (int)sLen * 2 > end) continue;
            bool valid = true;
            for (int k = 0; k < (int)sLen && valid; k++)
            {
                byte lo = d[j + 4 + k * 2], hi = d[j + 4 + k * 2 + 1];
                if (hi > 0x04 && hi != 0xF7) valid = false;
                if (hi == 0 && lo < 0x20 && lo != 0x09) valid = false;
            }
            if (!valid) continue;
            string s = Encoding.Unicode.GetString(d, j + 4, (int)sLen * 2);
            if (s.Length >= 3 && s.Contains('.'))
            {
                allStrings.Add((j, s));
                j += 3 + (int)sLen * 2;
            }
        }

        // Group into triples: spec -> symbol -> unit, with re-sync on misalignment
        for (int i = 0; i < allStrings.Count - 2; i += 3)
        {
            var (off0, spec) = allStrings[i];
            var (_, symbol) = allStrings[i + 1];
            var (off2, unit) = allStrings[i + 2];

            if (!spec.StartsWith("autodesk."))
                continue;
            // Re-sync if grouping is off
            if (spec.Contains("unit.symbol:") || spec.Contains("unit.unit:"))
            { i -= 2; continue; }

            // Validate triple: symbol must be unit.symbol:*, unit must be unit.unit:*
            if (!symbol.Contains("unit.symbol:") || !unit.Contains("unit.unit:"))
                continue; // Malformed triple (missing symbol or unit), skip

            double factor = 0;
            int afterUnit = off2 + 4 + unit.Length * 2;
            if (afterUnit + 8 <= end)
                factor = BitConverter.ToDouble(d, afterUnit);

            registry.TryAdd(spec, new ForgeSpecEntry(symbol, unit, factor));
        }
    }

    /// <summary>
    /// Decode a dimension/constraint reference block (0x091F or 0x009D) into ordered
    /// reference points. The element id is embedded at data offset 0x24, followed by null
    /// references, then a 0xFFFFFFFC (-4) marker. A u32 point count sits 7 bytes after the
    /// marker; the reference-point list (that many consecutive [x,y,z] f64 triples in feet,
    /// world coords) begins at marker+11. Consecutive points form measured segments;
    /// coincident points (zero-length segments) indicate alignment/lock constraints.
    /// </summary>
    private static void ExtractDimensionData(byte[] d, int start, int end, uint eid,
        Dictionary<uint, Model.DimensionData> dims)
    {
        int len = end - start;
        if (len < 0x30) return;

        // Locate the -4 (0xFFFFFFFC) marker that precedes the point list.
        int mk = -1;
        for (int i = start + 0x24; i + 4 <= end; i++)
        {
            if (d[i] == 0xFC && d[i + 1] == 0xFF && d[i + 2] == 0xFF && d[i + 3] == 0xFF) { mk = i; break; }
        }
        if (mk < 0 || mk + 11 > end) return;

        uint count = BitConverter.ToUInt32(d, mk + 7);
        if (count < 2 || count > 64) return;

        int o = mk + 11; // point list begins 11 bytes after the marker
        var pts = new List<double[]>();
        for (int i = 0; i < count && o + 24 <= end; i++, o += 24)
        {
            double x = BitConverter.ToDouble(d, o);
            double y = BitConverter.ToDouble(d, o + 8);
            double z = BitConverter.ToDouble(d, o + 16);
            bool valid = !double.IsNaN(x) && !double.IsNaN(y) && !double.IsNaN(z)
                && Math.Abs(x) < 1e4 && Math.Abs(y) < 1e4 && Math.Abs(z) < 1e4;
            if (!valid) break;
            pts.Add(new[] { x, y, z });
        }
        if (pts.Count < 2) return;

        var data = new Model.DimensionData { Points = pts };
        for (int i = 1; i < pts.Count; i++)
        {
            double dx = pts[i][0] - pts[i - 1][0];
            double dy = pts[i][1] - pts[i - 1][1];
            double dz = pts[i][2] - pts[i - 1][2];
            data.Segments.Add(Math.Sqrt(dx * dx + dy * dy + dz * dz));
        }
        data.Value = data.Segments.Count > 0 ? data.Segments[0] : 0;
        dims[eid] = data;
    }

    /// <summary>
    /// Decode an OST_SketchLines (0x0346) curve. Sketch lines store their geometry in the
    /// same GInfoBlob form as wall location curves: a 16-byte header
    /// [Tag:i32 (0=GLine, 1=GArc)][CtrlCmd:i32=0][GStyleElemId:i32=-1][Flags:u32=0x01080004],
    /// then 2 endParam doubles, then GLine = origin(3 f64) + unit direction(3 f64), or
    /// GArc = xDir(3) + yDir(3) + radius(1) + center(3). Coordinates are world feet.
    /// Result is stored as a <see cref="Model.LocationCurve"/> (start/end for lines,
    /// center/radius/endpoints for arcs).
    /// </summary>
    internal static void ExtractSketchCurve(byte[] d, int start, int end, uint eid,
        Dictionary<uint, Model.LocationCurve> sketchCurves,
        Dictionary<uint, int>? detailStyles = null)
    {
        for (int i = start; i + 16 <= end; i++)
        {
            int tag = BitConverter.ToInt32(d, i);
            if (tag != 0 && tag != 1) continue;
            if (BitConverter.ToInt32(d, i + 4) != 0) continue;             // CtrlCmd
            // GStyleElemId: -1 for sketch curves; a real line-style element id for
            // detail/model lines (which reuse the same GInfoBlob encoding).
            int gStyle = BitConverter.ToInt32(d, i + 8);
            if (gStyle != -1 && (gStyle <= 0 || gStyle >= 0x01400000)) continue;
            if (BitConverter.ToUInt32(d, i + 12) != 0x01080004) continue;  // Flags

            int pos = i + 16;
            if (pos + 16 > end) return;
            double ep0 = BitConverter.ToDouble(d, pos);
            double ep1 = BitConverter.ToDouble(d, pos + 8);
            pos += 16;
            if (double.IsNaN(ep0) || double.IsNaN(ep1) || Math.Abs(ep0) > 1e6 || Math.Abs(ep1) > 1e6) return;

            if (tag == 0) // GLine
            {
                if (pos + 48 > end) return;
                double ox = BitConverter.ToDouble(d, pos), oy = BitConverter.ToDouble(d, pos + 8), oz = BitConverter.ToDouble(d, pos + 16);
                double dx = BitConverter.ToDouble(d, pos + 24), dy = BitConverter.ToDouble(d, pos + 32), dz = BitConverter.ToDouble(d, pos + 40);
                double dl = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (dl < 0.9 || dl > 1.1) return;                  // direction must be unit
                if (Math.Abs(ep1 - ep0) < 1e-6) return;            // non-degenerate segment
                if (Math.Abs(ox) > 1e6 || Math.Abs(oy) > 1e6 || Math.Abs(oz) > 1e6) return;

                sketchCurves[eid] = new Model.LocationCurve
                {
                    CurveType = "Line",
                    StartPoint = [ox + dx * ep0, oy + dy * ep0, oz + dz * ep0],
                    EndPoint = [ox + dx * ep1, oy + dy * ep1, oz + dz * ep1],
                    Direction = [dx, dy, dz],
                    Length = Math.Abs(ep1 - ep0),
                };
                if (gStyle > 0 && detailStyles != null) detailStyles[eid] = gStyle;
                return;
            }
            else // GArc
            {
                if (pos + 80 > end) return;
                double xdx = BitConverter.ToDouble(d, pos), xdy = BitConverter.ToDouble(d, pos + 8), xdz = BitConverter.ToDouble(d, pos + 16);
                double ydx = BitConverter.ToDouble(d, pos + 24), ydy = BitConverter.ToDouble(d, pos + 32), ydz = BitConverter.ToDouble(d, pos + 40);
                double radius = BitConverter.ToDouble(d, pos + 48);
                double cx = BitConverter.ToDouble(d, pos + 56), cy = BitConverter.ToDouble(d, pos + 64), cz = BitConverter.ToDouble(d, pos + 72);

                double xl = Math.Sqrt(xdx * xdx + xdy * xdy + xdz * xdz);
                double yl = Math.Sqrt(ydx * ydx + ydy * ydy + ydz * ydz);
                if (xl < 0.9 || xl > 1.1 || yl < 0.9 || yl > 1.1) return;   // axes must be unit
                if (double.IsNaN(radius) || radius < 0.01 || radius > 1e6) return;
                if (Math.Abs(cx) > 1e6 || Math.Abs(cy) > 1e6 || Math.Abs(cz) > 1e6) return;
                if (Math.Abs(ep1 - ep0) < 1e-6) return;

                // Arc endpoints: center + radius*(cos(t)*xDir + sin(t)*yDir).
                double[] At(double t) =>
                [
                    cx + radius * (Math.Cos(t) * xdx + Math.Sin(t) * ydx),
                    cy + radius * (Math.Cos(t) * xdy + Math.Sin(t) * ydy),
                    cz + radius * (Math.Cos(t) * xdz + Math.Sin(t) * ydz),
                ];
                sketchCurves[eid] = new Model.LocationCurve
                {
                    CurveType = "Arc",
                    StartPoint = At(ep0),
                    EndPoint = At(ep1),
                    ArcCenter = [cx, cy, cz],
                    ArcRadius = radius,
                    Length = Math.Abs(ep1 - ep0) * radius,
                };
                if (gStyle > 0 && detailStyles != null) detailStyles[eid] = gStyle;
                return;
            }
        }
    }

    // Decode an OST_Materials (0x09B6) block into the material's shading appearance.
    // Every material owns a 0x09B6 block (small ~775 B for standalone definitions, large
    // ~6 KB for system/loaded materials). Layout: header, then the material NAME (first
    // length-prefixed UTF-16 string). After the name: [8 zero bytes][transparency f32]
    // [smoothness f32], and further on the shading entry
    // [FFFFFFFF][00000000][color u32 = 0x00RRGGBB][shininess u32][holderId][FFFFFFFF].
    // (0, 0) color/shininess pairs are placeholder noise and skipped; the trailing holder id
    // plus FFFFFFFF is required, which makes the signature strong enough to scan without a
    // name anchor when the name is missing.
    /// <summary>Length-prefixed keyed-string signatures that precede the material's DISPLAY
    /// name inside its 0x09B6 block (the first string of the large layout is the
    /// appearance-asset name, not the material name).</summary>
    private static readonly byte[] MatNameTagA = [0x0D, 0xB9, 0xF0, 0xFF, 0x00, 0x00, 0x00, 0x00];
    private static readonly byte[] MatNameTagB = [0xFF, 0xFF, 0xFF, 0xFF, 0xF0, 0x01];
    /// <summary>Tag A with the object reference [01 00 00 00][FFFFFFFF][tag 0x05C3] some large layouts (the XS_ and XT_
    /// insulation materials) put between the tag and the length of the name. Used for the position of the finish only.</summary>
    private static readonly byte[] MatNameTagC = [0x0D, 0xB9, 0xF0, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xC3, 0x05];

    internal static void ExtractMaterialAppearance(byte[] d, int start, int end, uint eid,
        Dictionary<uint, MaterialAppearance> mats, Dictionary<uint, (int Transparency, int Smoothness)> finishes)
    {
        // Anchor string = first valid length-prefixed UTF-16 string after the header.
        // In the SMALL layout this is the material name and anchors transparency/smoothness;
        // in the LARGE layout it is the appearance-asset name / texture path.
        string? anchor = null;
        int nameEnd = -1;
        for (int j = start + 0x26; j + 6 <= end && j < start + 0x100; j++)
        {
            uint sl = BitConverter.ToUInt32(d, j);
            if (sl < 1 || sl > 100 || j + 4 + (int)sl * 2 > end) continue;
            bool ok = true; bool alpha = false;
            for (int k = 0; k < (int)sl && ok; k++)
            {
                byte hi = d[j + 4 + k * 2 + 1], lo = d[j + 4 + k * 2];
                if (hi != 0 && hi != 0x04) { ok = false; break; }
                if (hi == 0 && (lo < 0x20 || lo > 0x7E)) { ok = false; break; }
                if (char.IsLetter((char)(lo | (hi << 8)))) alpha = true;
            }
            if (!ok || !alpha) continue;
            anchor = System.Text.Encoding.Unicode.GetString(d, j + 4, (int)sl * 2);
            nameEnd = j + 4 + (int)sl * 2;
            break;
        }

        // Display name: keyed-string tags take priority; the anchor
        // string is a fallback accepted only when it does not look like a texture path/keynote.
        string? taggedNameA = FindTaggedString(d, start, end, MatNameTagA, out int taggedNameEndA);
        string? taggedNameB = FindTaggedString(d, start, end, MatNameTagB, out int taggedNameEndB);
        string? name = taggedNameA
                    ?? taggedNameB
                    ?? (IsCleanMaterialName(anchor) ? anchor : null);

        // Transparency / smoothness follow the display name, not necessarily the first
        // appearance-asset string. The tagged display-name end is therefore the authoritative
        // schema position whenever that field is present.
        int taggedNameEndC = -1;
        string? taggedNameC = taggedNameA == null ? FindTaggedString(d, start, end, MatNameTagC, out taggedNameEndC) : null;
        int finishNameEnd = taggedNameA != null ? taggedNameEndA
            : taggedNameC != null ? taggedNameEndC
            : taggedNameB != null ? taggedNameEndB : nameEnd;
        int transparency = -1, smoothness = 0;
        float transparencyFraction = -1f;
        bool finishDecoded = finishNameEnd > 0
            && TryDecodeMaterialFinish(d, finishNameEnd, end, out transparency, out smoothness, out transparencyFraction);
        if (!finishDecoded)
            TryFindMaterialFinish(d, start, end, out transparency, out smoothness, out transparencyFraction);

        // Shading entry: [FFFFFFFF][00000000][color][shininess][holderId][FFFFFFFF],
        // skipping placeholder (0,0) pairs. Scan from the name if found, else the block start.
        int color = -1, shininess = 0;
        for (int j = nameEnd > 0 ? nameEnd : start; j + 28 <= end; j++)
        {
            if (BitConverter.ToUInt32(d, j) != 0xFFFFFFFF || BitConverter.ToUInt32(d, j + 4) != 0) continue;
            uint c = BitConverter.ToUInt32(d, j + 8);
            uint s = BitConverter.ToUInt32(d, j + 12);
            uint hid = BitConverter.ToUInt32(d, j + 16);
            if (c == 0xFFFFFFFF || (c >> 24) != 0 || s > 255) continue;
            if (c == 0 && s == 0) continue;                       // placeholder noise
            if (!ElementIdEncoding.IsSupported(hid)) continue;    // must reference a holder
            if (BitConverter.ToUInt32(d, j + 20) != 0xFFFFFFFF) continue;
            color = (int)c; shininess = (int)s;
            break;
        }
        if (color < 0)
        {
            // No shading entry in this block: the colour comes from the holder vote, the finish is still the material's own.
            if (transparency >= 0) finishes.TryAdd(eid, (transparency, smoothness));
            return;
        }

        mats[eid] = new MaterialAppearance(name, color, shininess, transparency, smoothness, transparencyFraction);
    }

    /// <summary>Decode the schema-defined material transparency/smoothness layouts.
    /// Returned values use the integer-percent storage type of the Revit API.</summary>
    internal static bool TryDecodeMaterialFinish(byte[] data, int nameEnd, int end,
        out int transparency, out int smoothness)
        => TryDecodeMaterialFinish(data, nameEnd, end, out transparency, out smoothness, out _);

    /// <summary>The finish of a material whose display name could not be located (a name longer than the 100 characters the tagged
    /// strings allow, a first string that is a texture path): the text fields of the material (name, description, comments, keynote,
    /// asset name) are length-prefixed UTF-16 strings and the finish follows the last of them, before the header of the appearance
    /// asset. The first string end before that header that one of the layouts of <see cref="TryDecodeMaterialFinish(byte[], int, int, out int, out int, out float)"/>
    /// accepts is the finish.</summary>
    internal static bool TryFindMaterialFinish(byte[] d, int start, int end,
        out int transparency, out int smoothness, out float transparencyFraction)
    {
        transparency = -1;
        smoothness = 0;
        transparencyFraction = -1f;
        const int MaxTextLength = 500;
        for (int j = start; j + 6 <= end;)
        {
            uint length = BitConverter.ToUInt32(d, j);
            if (length is >= 1 and <= MaxTextLength && j + 4 + (int)length * 2 <= end && IsTextRun(d, j + 4, (int)length))
            {
                int stringEnd = j + 4 + (int)length * 2;
                if (System.Text.Encoding.Unicode.GetString(d, j + 4, (int)length * 2) == MaterialAssetHeader) return false;
                if (TryDecodeMaterialFinish(d, stringEnd, end, out transparency, out smoothness, out transparencyFraction))
                    return true;
                j = stringEnd;
            }
            else j++;
        }
        transparency = -1;
        smoothness = 0;
        transparencyFraction = -1f;
        return false;
    }

    /// <summary>The first string of the appearance asset of a material; the text fields end before it.</summary>
    private const string MaterialAssetHeader = "assetlibrary_base.fbx";

    private static bool IsTextRun(byte[] d, int offset, int length)
    {
        for (int k = 0; k < length; k++)
        {
            byte low = d[offset + 2 * k], high = d[offset + 2 * k + 1];
            if (high != 0 && high != 0x04) return false;
            if (high == 0 && (low < 0x20 || low > 0x7E)) return false;
        }
        return true;
    }

    /// <param name="transparencyFraction">The transparency as stored, a float in 0..1 (the integer percent truncates 0.975
    /// to 97); -1 when the layout was not decoded.</param>
    internal static bool TryDecodeMaterialFinish(byte[] data, int nameEnd, int end,
        out int transparency, out int smoothness, out float transparencyFraction)
    {
        transparency = -1;
        smoothness = 0;
        transparencyFraction = -1f;
        int valueOffset;
        if (nameEnd >= 0 && nameEnd + 16 <= end
            && BitConverter.ToUInt64(data, nameEnd) == 0)
        {
            valueOffset = nameEnd + 8;
        }
        else if (nameEnd >= 0 && nameEnd + 18 <= end
            && BitConverter.ToUInt32(data, nameEnd) == 0xFFFFFFFF
            && BitConverter.ToUInt16(data, nameEnd + 4) == 2960
            && BitConverter.ToUInt32(data, nameEnd + 6) == 0)
        {
            valueOffset = nameEnd + 10;
        }
        else if (nameEnd >= 0 && nameEnd + 20 <= end
            && BitConverter.ToUInt32(data, nameEnd) == 0xFFFFFFFF
            && BitConverter.ToUInt16(data, nameEnd + 4) == 2960
            && BitConverter.ToUInt32(data, nameEnd + 6) == 0xFFFFFFFF
            && BitConverter.ToUInt16(data, nameEnd + 10) == 2880)
        {
            // PhysicalParamSet owns a serialized ParamSet pointer before the two values.
            valueOffset = nameEnd + 12;
        }
        else if (nameEnd >= 0 && nameEnd + 18 <= end
            && BitConverter.ToUInt32(data, nameEnd) == 0
            && BitConverter.ToUInt32(data, nameEnd + 4) == 0xFFFFFFFF
            && BitConverter.ToUInt16(data, nameEnd + 8) == 2880)
        {
            // A zero word and the nested ParamSet pointer (class 2880) before the two values (the ADSK_ materials).
            valueOffset = nameEnd + 10;
        }
        else
        {
            return false;
        }

        float transparencyValue = BitConverter.ToSingle(data, valueOffset);
        float smoothnessValue = BitConverter.ToSingle(data, valueOffset + 4);
        if (!float.IsFinite(transparencyValue) || transparencyValue < 0
            || transparencyValue > 1.0f || !float.IsFinite(smoothnessValue)
            || smoothnessValue < 0 || smoothnessValue > 1.0f)
            return false;

        transparency = Math.Clamp((int)(transparencyValue * 100), 0, 100);
        smoothness = Math.Clamp((int)(smoothnessValue * 100), 0, 100);
        transparencyFraction = transparencyValue;
        return true;
    }

    /// <summary>Find a length-prefixed UTF-16 string immediately following a tag signature.</summary>
    private static string? FindTaggedString(byte[] d, int start, int end, byte[] tag,
        out int stringEnd)
    {
        stringEnd = -1;
        for (int j = start; j + tag.Length + 6 <= end; j++)
        {
            bool m = true;
            for (int k = 0; k < tag.Length && m; k++) if (d[j + k] != tag[k]) m = false;
            if (!m) continue;
            int o = j + tag.Length;
            uint ln = BitConverter.ToUInt32(d, o);
            if (ln < 1 || ln > 100 || o + 4 + (int)ln * 2 > end) return null;
            string s = System.Text.Encoding.Unicode.GetString(d, o + 4, (int)ln * 2);
            bool alpha = false, ok = true;
            foreach (char c in s)
            {
                if (char.IsLetter(c)) alpha = true;
                if (char.IsControl(c) || c >= 0x2000) { ok = false; break; }
            }
            if (!ok || !alpha) return null;
            stringEnd = o + 4 + (int)ln * 2;
            return s;
        }
        return null;
    }

    /// <summary>Reject anchor strings that are texture paths, keynote codes, URLs or CJK
    /// garbage rather than a material display name.</summary>
    private static bool IsCleanMaterialName(string? nm)
    {
        if (string.IsNullOrEmpty(nm) || nm.Length < 3) return false;
        if (nm.Contains('\\') || nm.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(nm, @"\.(png|jpg|jpeg|bmp|fbx)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(nm, @"^[A-Z]\d{1,3}$")) return false;
        foreach (char c in nm) if (c >= 0x2000) return false;
        return true;
    }

    /// <summary>
    /// Collect all shading entries [FFFFFFFF][00000000][color][shininess][holderId][FFFFFFFF]
    /// in a chunk into holderId -> (color, shininess) votes. Used as a fallback to color
    /// materials that have no decodable 0x09B6 block of their own: the material's record
    /// references include its shading-holder element id.
    /// </summary>
    internal static void CollectShadingEntries(byte[] d,
        Dictionary<uint, Dictionary<(int Color, int Shininess), int>> entries)
    {
        for (int j = 0; j + 28 <= d.Length; j++)
        {
            if (BitConverter.ToUInt32(d, j) != 0xFFFFFFFF || BitConverter.ToUInt32(d, j + 4) != 0) continue;
            uint c = BitConverter.ToUInt32(d, j + 8);
            uint s = BitConverter.ToUInt32(d, j + 12);
            uint hid = BitConverter.ToUInt32(d, j + 16);
            if (c == 0xFFFFFFFF || (c >> 24) != 0 || s > 255) continue;
            if (c == 0 && s == 0) continue;
            if (!ElementIdEncoding.IsSupported(hid)) continue;
            if (BitConverter.ToUInt32(d, j + 20) != 0xFFFFFFFF) continue;
            if (!entries.TryGetValue(hid, out var votes)) entries[hid] = votes = new();
            var key = ((int)c, (int)s);
            votes.TryGetValue(key, out int n); votes[key] = n + 1;
        }
    }

    /// <summary>
    /// Extract the sheet number + issue date from a 0x03E3 view-info block. Signature within
    /// the first 0x400 bytes: [8 zero bytes][len:u32 1..12][ASCII-as-UTF16 short string]
    /// [len:u32 1..20][date string]. Only ViewSheet blocks carry this pair; the caller applies
    /// the result to OST_Sheets elements only.
    /// </summary>
    private static void ExtractSheetInfo(byte[] d, int start, int end, uint eid,
        Dictionary<uint, (string Number, string? IssueDate)> sheets)
    {
        for (int o = start + 0x100; o + 4 <= end && o < start + 0x400; o++)
        {
            bool zeros = true;
            for (int k = 1; k <= 8 && zeros; k++) if (d[o - k] != 0) zeros = false;
            if (!zeros) continue;
            uint ln = BitConverter.ToUInt32(d, o);
            if (ln < 1 || ln > 12 || o + 4 + (int)ln * 2 > end) continue;
            bool ascii = true;
            for (int k = 0; k < (int)ln && ascii; k++)
                if (d[o + 4 + k * 2 + 1] != 0 || d[o + 4 + k * 2] < 0x20 || d[o + 4 + k * 2] > 0x7E) ascii = false;
            if (!ascii) continue;
            string num = System.Text.Encoding.Unicode.GetString(d, o + 4, (int)ln * 2);

            string? date = null;
            int o2 = o + 4 + (int)ln * 2;
            if (o2 + 4 <= end)
            {
                uint ln2 = BitConverter.ToUInt32(d, o2);
                if (ln2 >= 1 && ln2 <= 20 && o2 + 4 + (int)ln2 * 2 <= end)
                {
                    bool ok2 = true;
                    for (int k = 0; k < (int)ln2 && ok2; k++)
                        if (d[o2 + 4 + k * 2 + 1] != 0 || d[o2 + 4 + k * 2] < 0x20 || d[o2 + 4 + k * 2] > 0x7E) ok2 = false;
                    if (ok2) date = System.Text.Encoding.Unicode.GetString(d, o2 + 4, (int)ln2 * 2);
                }
            }
            sheets[eid] = (num, date);
            return;
        }
    }

    /// <summary>Scans the blocks of a chunk for image-symbol blocks (class 0x089A) and records the pixel size of each
    /// image.</summary>
    internal static void CollectImageSizes(byte[] chunk, ParsingState state)
    {
        int pos = 0;
        while (pos + 16 <= chunk.Length)
        {
            uint eid = BitConverter.ToUInt32(chunk, pos);
            int size = BitConverter.ToInt32(chunk, pos + 8);
            uint classIdx = BitConverter.ToUInt32(chunk, pos + 12);
            if (eid == 0 || eid > 50_000_000 || size <= 0 || size > MaxBlockSize || pos + 16 + size > chunk.Length)
            {
                pos++;
                continue;
            }
            if ((classIdx & 0xFFFF) == 0x089A && size >= 0x88 && !state.ImageSizes.ContainsKey(eid))
            {
                int width = BitConverter.ToInt32(chunk, pos + 16 + 0x80);
                int height = BitConverter.ToInt32(chunk, pos + 16 + 0x84);
                if (width is > 0 and < 20000 && height is > 0 and < 20000)
                    state.ImageSizes[eid] = (width, height);
            }
            pos += 16 + size;
        }
    }

    /// <summary>
    /// Scans the blocks of a chunk for the plane of a view: the schema stores m_horzDir (a horizontal unit vector), the
    /// view origin, the view scale, m_vertDir (a vertical unit vector) and m_viewDir (a horizontal unit vector) as
    /// thirteen consecutive doubles. The block walk of the project's own data does not always reach them, so they are
    /// found by signature and kept under the element id of the block.
    /// </summary>
    internal static void CollectViewPlanes(byte[] chunk, ParsingState state)
    {
        int pos = 0;
        while (pos + 16 <= chunk.Length)
        {
            uint eid = BitConverter.ToUInt32(chunk, pos);
            int size = BitConverter.ToInt32(chunk, pos + 8);
            uint classIdx = BitConverter.ToUInt32(chunk, pos + 12);
            if (eid == 0 || eid > 50_000_000 || size <= 0 || size > MaxBlockSize || pos + 16 + size > chunk.Length)
            {
                pos++;
                continue;
            }
            if (!state.ViewPlanes.ContainsKey(eid))
                TryFindViewPlane(chunk, pos + 16, pos + 16 + size, eid, state);
            pos += 16 + size;
        }
    }

    private static void TryFindViewPlane(byte[] d, int start, int end, uint eid, ParsingState state)
    {
        for (int i = start; i + 13 * 8 <= end; i++)
        {
            double hx = BitConverter.ToDouble(d, i), hy = BitConverter.ToDouble(d, i + 8);
            double hz = BitConverter.ToDouble(d, i + 16);
            if (!double.IsFinite(hx) || !double.IsFinite(hy) || !double.IsFinite(hz)) continue;
            double hn = Math.Sqrt(hx * hx + hy * hy + hz * hz);
            if (hn is < 0.999 or > 1.001 || Math.Abs(hz) > 0.01) continue;
            double ox = BitConverter.ToDouble(d, i + 24), oy = BitConverter.ToDouble(d, i + 32);
            double oz = BitConverter.ToDouble(d, i + 40);
            if (!double.IsFinite(ox) || !double.IsFinite(oy) || !double.IsFinite(oz)
                || Math.Abs(ox) > 1e5 || Math.Abs(oy) > 1e5 || Math.Abs(oz) > 1e5) continue;
            double scale = BitConverter.ToDouble(d, i + 48);
            if (!double.IsFinite(scale) || scale < 1 || scale > 1000
                || Math.Abs(scale - Math.Round(scale)) > 1e-6) continue;
            double vx = BitConverter.ToDouble(d, i + 56), vy = BitConverter.ToDouble(d, i + 64);
            double vz = BitConverter.ToDouble(d, i + 72);
            if (!double.IsFinite(vx) || !double.IsFinite(vy) || !double.IsFinite(vz)) continue;
            double vn = Math.Sqrt(vx * vx + vy * vy + vz * vz);
            if (vn is < 0.999 or > 1.001 || Math.Abs(vx) > 0.01 || Math.Abs(vy) > 0.01) continue;
            double dx = BitConverter.ToDouble(d, i + 80), dy = BitConverter.ToDouble(d, i + 88);
            double dz = BitConverter.ToDouble(d, i + 96);
            if (!double.IsFinite(dx) || !double.IsFinite(dy) || !double.IsFinite(dz)) continue;
            double dn = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (dn is < 0.999 or > 1.001 || Math.Abs(dz) > 0.01) continue;
            if (Math.Abs(hx * dx + hy * dy + hz * dz) > 0.01) continue;
            state.ViewPlanes[eid] = ([ox, oy, oz], [hx, hy, hz], [vx, vy, vz], [dx, dy, dz]);
            return;
        }
    }

    /// <summary>
    /// Scans a chunk for text-note blocks (class 0x0F83, little-endian 83 0F at the class field) and records the text
    /// and position of every note whose block the block walk did not reach. The two class bytes are rare in text, so
    /// the scan follows them instead of stepping through every byte.
    /// </summary>
    internal static void CollectTextNotes(byte[] chunk, ParsingState state)
    {
        ReadOnlySpan<byte> marker = [0x83, 0x0F];
        int at = chunk.AsSpan().IndexOf(marker);
        while (at >= 0)
        {
            int blockStart = at - 12;
            if (blockStart >= 0 && blockStart + 16 <= chunk.Length)
            {
                uint eid = BitConverter.ToUInt32(chunk, blockStart);
                int size = BitConverter.ToInt32(chunk, blockStart + 8);
                uint classIdx = BitConverter.ToUInt32(chunk, blockStart + 12);
                if (eid is > 0 and < 50_000_000 && size > 0 && size <= MaxBlockSize
                    && blockStart + 16 + size <= chunk.Length && (classIdx & 0xFFFF) == 0x0F83
                    && !state.TextNotes.ContainsKey(eid))
                    ExtractTextNote(chunk, blockStart + 16, blockStart + 16 + size, eid, state);
            }
            int next = chunk.AsSpan(at + 2).IndexOf(marker);
            at = next < 0 ? -1 : at + 2 + next;
        }
    }

    /// <summary>
    /// Extract the placement of a viewport: the displayed view id and the offset on the sheet (feet). The fields are
    /// read from the element payload, two bytes before the block data.
    /// </summary>
    private static void ExtractViewport(byte[] d, int dataStart, uint eid, ParsingState state)
    {
        int payload = dataStart - 2;
        double x = BitConverter.ToDouble(d, payload + 0x47);
        double y = BitConverter.ToDouble(d, payload + 0x4F);
        double z = BitConverter.ToDouble(d, payload + 0x57);
        int viewId = BitConverter.ToInt32(d, payload + 0x8F);
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) return;
        if (Math.Abs(x) > 1e5 || Math.Abs(y) > 1e5 || viewId <= 0 || viewId > 50_000_000) return;
        state.ViewportPlacements[eid] = (viewId, x, y, z);
    }

    /// <summary>
    /// Extract the plain text and the position of a text note from its 0x0F83 block. The block holds an RTF string
    /// ("{\\rtf1...") and, before it, [0x0271][x:f64][y:f64], the position of the note in feet.
    /// </summary>
    private static void ExtractTextNote(byte[] d, int start, int end, uint eid, ParsingState state)
    {
        int rtf = -1;
        for (int i = start; i + 7 <= end; i++)
        {
            if (d[i] == (byte)'{' && d[i + 1] == (byte)'\\' && d[i + 2] == (byte)'r' && d[i + 3] == (byte)'t'
                && d[i + 4] == (byte)'f' && d[i + 5] == (byte)'1')
            {
                rtf = i;
                break;
            }
        }
        if (rtf < 0) return;
        if (RtfText.Parse(d, rtf, end) is not { Text.Length: > 0 } parsed) return;

        double x = 0, y = 0;
        bool positioned = false;
        for (int i = start; i + 18 <= rtf; i++)
        {
            if (d[i] != 0x71 || d[i + 1] != 0x02) continue;
            double px = BitConverter.ToDouble(d, i + 2), py = BitConverter.ToDouble(d, i + 10);
            if (!double.IsFinite(px) || !double.IsFinite(py) || Math.Abs(px) > 1e6 || Math.Abs(py) > 1e6) continue;
            x = px; y = py; positioned = true;
            break;
        }
        if (!positioned) return;
        state.TextNotes[eid] = (parsed.Text, x, y, parsed.FontSizePoints);
    }

    /// <summary>
    /// Extract the display name from an element's own block: the generic name-field signature
    /// is [FFFFFFFF][tag 0x0BB5][u32 = 1][u32 holderId][u32 = 0][len:u32][UTF-16 name].
    /// For views this is the exact VIEW_NAME.
    /// </summary>
    private static void ExtractViewName(byte[] d, int start, int end, uint eid,
        Dictionary<uint, string> names)
    {
        for (int j = start; j + 30 <= end; j++)
        {
            if (BitConverter.ToUInt32(d, j) != 0xFFFFFFFF) continue;
            if (BitConverter.ToUInt16(d, j + 4) != 0x0BB5) continue;
            if (BitConverter.ToUInt32(d, j + 6) != 1) continue;
            if (BitConverter.ToUInt32(d, j + 14) != 0) continue;
            uint ln = BitConverter.ToUInt32(d, j + 18);
            if (ln < 1 || ln > 100 || j + 22 + (int)ln * 2 > end) return;
            bool ok = true, alpha = false;
            for (int k = 0; k < (int)ln && ok; k++)
            {
                char c = (char)(d[j + 22 + k * 2] | (d[j + 22 + k * 2 + 1] << 8));
                if (char.IsControl(c) || c >= 0x2000) ok = false;
                else if (char.IsLetterOrDigit(c)) alpha = true;
            }
            if (ok && alpha)
                names[eid] = System.Text.Encoding.Unicode.GetString(d, j + 22, (int)ln * 2);
            return;
        }
    }

    /// <summary>HVACLoadSpaceType (0x085A) field table: each entry is (byte offset from the
    /// block data start, BIP id) for an f64 stored at a fixed position.</summary>
    private static readonly (int Offset, int Bip)[] HvacFields =
    {
        (0x47, -1114220), // ROOM_DESIGN_LIGHTING_LOAD_PER_AREA_PARAM
        (0x4F, -1114219), // ROOM_DESIGN_POWER_LOAD_PER_AREA_PARAM
        (0x57, -1114709), // SPACE_COOLING_SET_POINT
        (0x5F, -1154671), // ROOM_AIR_CHANGES_PER_HOUR_PARAM
        (0x67, -1114175), // ROOM_AREA_PER_PERSON_PARAM
        (0x77, -1114348), // SPACE_INFILTRATION_PARAM
        (0x7F, -1114189), // ROOM_PEOPLE_LATENT_HEAT_GAIN_PER_PERSON_PARAM
        (0x87, -1154668), // ROOM_OUTDOOR_AIR_PER_AREA_PARAM
        (0x8F, -1154665), // ROOM_OUTDOOR_AIR_PER_PERSON_PARAM
        (0x97, -1150188), // ROOM_PLENUM_LIGHTING_PARAM
        (0x9F, -1114239), // ROOM_PEOPLE_SENSIBLE_HEAT_GAIN_PER_PERSON_PARAM
        (0xA7, -1114711), // SPACE_DEHUMIDIFICATION_SET_POINT
        (0xAF, -1114708), // SPACE_HEATING_SET_POINT
        (0xB7, -1114710), // SPACE_HUMIDIFICATION_SET_POINT
    };

    /// <summary>
    /// Decode an HVACLoadSpaceType (0x085A) block: the self element id is at data offset 0x24,
    /// followed by a fixed-offset table of f64 energy-load parameters (see <see cref="HvacFields"/>).
    /// </summary>
    private static void ExtractHvacSpaceType(byte[] d, int start, int end, uint eid,
        Dictionary<uint, Dictionary<int, double>> hvac,
        Dictionary<uint, (int Power, int Occupancy, int Lighting)> schedules)
    {
        if (start + 0xB7 + 8 > end) return;
        var vals = new Dictionary<int, double>();
        foreach (var (off, bip) in HvacFields)
        {
            double v = BitConverter.ToDouble(d, start + off);
            if (double.IsNaN(v) || double.IsInfinity(v) || Math.Abs(v) > 1e6) continue;
            vals[bip] = v;
        }
        if (vals.Count > 0) hvac[eid] = vals;

        // Schedule element references at fixed offsets after the f64 table:
        // 0xC3 = power schedule, 0xC7 = occupancy schedule, 0xCB = lighting schedule.
        if (start + 0xCB + 4 <= end)
        {
            int pw = BitConverter.ToInt32(d, start + 0xC3);
            int oc = BitConverter.ToInt32(d, start + 0xC7);
            int lt = BitConverter.ToInt32(d, start + 0xCB);
            if (pw > 0 && oc > 0 && lt > 0)
                schedules[eid] = (pw, oc, lt);
        }
    }

    /// <summary>
    /// Chunk-level scan for BIP-keyed strings inside elements' own blocks:
    /// [bipId:i32 (negative built-in parameter id)][len:u32][UTF-16 value].
    /// Source of per-element string parameters (marks, descriptions, comments, keynotes).
    /// </summary>
    internal static void CollectBipStrings(byte[] d, ParsingState state)
    {
        int pos = 0;
        while (pos + 16 <= d.Length)
        {
            uint eid = BitConverter.ToUInt32(d, pos);
            if (pos + 12 <= d.Length)
            {
                uint rb = BitConverter.ToUInt32(d, pos + 4);
                ushort mg = BitConverter.ToUInt16(d, pos + 8);
                if ((mg == 0x053C || mg == 0x04F8)
                    && ElementIdEncoding.IsSupported(eid) && rb > 0 && rb < 500_000
                    && pos + 12 + (int)rb <= d.Length)
                { pos += 12 + (int)rb; continue; }
            }
            uint dataSize = BitConverter.ToUInt32(d, pos + 8);
            if (dataSize > 1_000_000) { pos++; continue; }
            if (eid == 0) { pos += 4; continue; }
            if (dataSize == 0) { pos++; continue; }
            if (pos + 16 + (int)dataSize > d.Length) break;

            if (ElementIdEncoding.IsSupported(eid))
            {
                int ds = pos + 16, de = ds + (int)dataSize;
                for (int j = ds; j + 10 <= de; j++)
                {
                    int bip = BitConverter.ToInt32(d, j);
                    if (bip > -1_000_000 || bip < -1_200_000) continue;
                    uint ln = BitConverter.ToUInt32(d, j + 4);
                    if (ln < 1 || ln > 400 || j + 8 + (int)ln * 2 > de) continue;
                    bool ok = true, alpha = false;
                    for (int k = 0; k < (int)ln && ok; k++)
                    {
                        char c = (char)(d[j + 8 + k * 2] | (d[j + 8 + k * 2 + 1] << 8));
                        // Revit also stores whitespace-encoded metadata (U+2000..U+206F, BOM)
                        // in some material description/keynote strings - accept those too.
                        bool plain = c >= 0x20 && c < 0x2000;
                        bool punct = (c >= 0x2000 && c <= 0x206F) || c == 0xFEFF;
                        if (!plain && !punct) ok = false;
                        else if (c > 0x20) alpha = true;
                    }
                    if (!ok || !alpha) continue;
                    if (!state.BipKeyedStrings.TryGetValue(eid, out var map))
                        state.BipKeyedStrings[eid] = map = new Dictionary<int, string>();
                    map.TryAdd(bip, System.Text.Encoding.Unicode.GetString(d, j + 8, (int)ln * 2));
                    j += 7 + (int)ln * 2;
                }
            }
            pos += 16 + (int)dataSize;
        }
    }

    /// <summary>
    /// Chunk-level scan for level-elevation candidates: the level's line record ends with
    /// [elevation:f64][-0.0:f64][-1.0:f64]; the elevation sits 8 bytes before the marker.
    /// The marker also occurs in non-level data, so candidates are gated by the element's
    /// category (OST_Levels) at application time. Owner = enclosing block's element id.
    /// </summary>
    internal static void CollectLevelElevations(byte[] d, ParsingState state)
    {
        // marker: -0.0 followed by -1.0
        byte[] mark = [0, 0, 0, 0, 0, 0, 0, 0x80, 0, 0, 0, 0, 0, 0, 0xF0, 0xBF];
        int pos = 0;
        while (pos + 16 <= d.Length)
        {
            uint eid = BitConverter.ToUInt32(d, pos);
            if (pos + 12 <= d.Length)
            {
                uint rb = BitConverter.ToUInt32(d, pos + 4);
                ushort mg = BitConverter.ToUInt16(d, pos + 8);
                if ((mg == 0x053C || mg == 0x04F8)
                    && ElementIdEncoding.IsSupported(eid) && rb > 0 && rb < 500_000
                    && pos + 12 + (int)rb <= d.Length)
                { pos += 12 + (int)rb; continue; }
            }
            uint dataSize = BitConverter.ToUInt32(d, pos + 8);
            if (dataSize > 1_000_000) { pos++; continue; }
            if (eid == 0) { pos += 4; continue; }
            if (dataSize == 0) { pos++; continue; }
            if (pos + 16 + (int)dataSize > d.Length) break;

            if (ElementIdEncoding.IsSupported(eid) && !state.LevelElevCandidates.ContainsKey(eid))
            {
                int ds = pos + 16, de = ds + (int)dataSize;
                for (int j = ds + 8; j + 16 <= de; j++)
                {
                    bool m = true;
                    for (int k = 0; k < 16 && m; k++) if (d[j + k] != mark[k]) m = false;
                    if (!m) continue;
                    double elev = BitConverter.ToDouble(d, j - 8);
                    if (!double.IsNaN(elev) && Math.Abs(elev) < 1e5)
                        state.LevelElevCandidates[eid] = elev;
                    break;
                }
            }
            pos += 16 + (int)dataSize;
        }
    }

    /// <summary>
    /// Chunk-level HVAC load-type scan, independent of block walking and classIdx - both are
    /// file-specific (the classIdx is a schema index that differs between files, and in some
    /// files the null-runs around the header are 0x3C-filled rather than 0xFF, desyncing the
    /// block walker). Signature at a candidate table start `o`: self element id at o+0x24 that
    /// is a KNOWN element, cooling/heating set points in Kelvin range at o+0x57 / o+0xAF,
    /// lighting load at o+0x47 in [0, 1e4), dehumidification set point at o+0xA7 in [0, 1].
    /// </summary>
    internal static void CollectHvacTables(byte[] d, HashSet<uint> knownIds,
        Dictionary<uint, Dictionary<int, double>> hvac,
        Dictionary<uint, (int Power, int Occupancy, int Lighting)> schedules)
    {
        for (int o = 0; o + 0xCF <= d.Length; o++)
        {
            uint eid = BitConverter.ToUInt32(d, o + 0x24);
            if (!knownIds.Contains(eid) || hvac.ContainsKey(eid)) continue;
            double cool = BitConverter.ToDouble(d, o + 0x57);
            double heat = BitConverter.ToDouble(d, o + 0xAF);
            if (!(cool > 250 && cool < 330 && heat > 250 && heat < 330)) continue;
            double ll = BitConverter.ToDouble(d, o + 0x47);
            double dh = BitConverter.ToDouble(d, o + 0xA7);
            if (double.IsNaN(ll) || ll < 0 || ll >= 1e4 || double.IsNaN(dh) || dh < 0 || dh > 1) continue;
            ExtractHvacSpaceType(d, o, Math.Min(d.Length, o + 0xD0), eid, hvac, schedules);
        }
    }

    /// <summary>Extract CompoundStructure layers from 0xFFFF**** blocks.
    /// Scans for marker 0x1003 followed by layer count and 29-byte layer entries:
    /// [thickness:f64][function:i32][-1:i32][materialId:i32][-1:i32][layerIndex:i32][wrapping:u8]
    /// </summary>
    internal static void ExtractCompoundLayers(byte[] d, int start, int end,
        uint eid, Dictionary<uint, List<CompoundLayer>> layers)
    {
        // Scan for 0x1003 marker (u16) followed by a plausible layer count
        for (int i = start; i + 6 <= end - 29; i += 2)
        {
            ushort marker = BitConverter.ToUInt16(d, i);
            if (marker != 0x1003) continue;

            int countPos = i + 2;
            if (countPos + 4 > end) continue;
            int layerCount = BitConverter.ToInt32(d, countPos);
            if (layerCount < 1 || layerCount > 50) continue;

            int dataPos = countPos + 4;
            if (dataPos + layerCount * 29 > end) continue;

            bool valid = true;
            var result = new List<CompoundLayer>(layerCount);
            for (int j = 0; j < layerCount; j++)
            {
                int off = dataPos + j * 29;
                double thickness = BitConverter.ToDouble(d, off);
                int func = BitConverter.ToInt32(d, off + 8);
                // d[off+12] should be -1 (padding)
                int matId = BitConverter.ToInt32(d, off + 16);
                // d[off+20] should be -1 (padding)
                int layerIdx = BitConverter.ToInt32(d, off + 24);

                // Validate: thickness in [0, 30] feet, func one of the stored layer functions, layerIdx matches j
                if (thickness < 0 || thickness > 30 || !CompoundLayer.IsStoredFunction(func) || layerIdx != j)
                {
                    valid = false;
                    break;
                }

                result.Add(new CompoundLayer
                {
                    ThicknessFt = thickness,
                    Function = func,
                    MaterialId = matId > 0 ? matId : 0,
                    LayerIndex = layerIdx
                });
            }

            if (valid && result.Count > 0)
            {
                layers[eid] = result;
                return;
            }
        }
    }
}

using System.Text;
using RvtIO;
using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public class BlockDiagnosticTests
{
    private readonly ITestOutputHelper _output;

    public BlockDiagnosticTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public void BlocksFor100907()
    {
        string rvtPath = TestFileFixture.FindTestFile("TESTFILE.rvt");
        Assert.True(File.Exists(rvtPath), $"Test file not found: {rvtPath}");

        var reader = new RvtFileReader();
        var (info, state) = reader.ReadWithState(rvtPath);

        const uint TARGET_ELEM_ID = 100907;
        var sb = new StringBuilder();
        sb.AppendLine($"=== Blocks for element {TARGET_ELEM_ID} ===");
        sb.AppendLine();

        int totalBlocks = 0;

        sb.AppendLine("--- Non-Record Chunks ---");
        totalBlocks += ScanChunksForBlocks(state.NonRecordChunks, TARGET_ELEM_ID, sb, "NonRecord");

        sb.AppendLine();
        sb.AppendLine("--- Record Chunks ---");
        totalBlocks += ScanChunksForBlocks(state.RecordChunks, TARGET_ELEM_ID, sb, "Record");

        sb.AppendLine();
        sb.AppendLine($"Total blocks found: {totalBlocks}");

        string result = sb.ToString();

        File.WriteAllText(TestFiles.Scratch("blocks_100907.txt"), result, Encoding.UTF8);

        _output.WriteLine(result);
        _output.WriteLine($"Output written to {TestFiles.Scratch("blocks_100907.txt")}");
    }

    private static int ScanChunksForBlocks(IReadOnlyList<byte[]> chunks, uint targetElemId, StringBuilder sb, string label)
    {
        int blockCount = 0;

        for (int ci = 0; ci < chunks.Count; ci++)
        {
            var chunk = chunks[ci];
            int pos = 0;

            while (pos + 16 <= chunk.Length)
            {
                uint elemId = BitConverter.ToUInt32(chunk, pos);
                uint hash = BitConverter.ToUInt32(chunk, pos + 4);
                uint dataSize = BitConverter.ToUInt32(chunk, pos + 8);
                uint classIdx = BitConverter.ToUInt32(chunk, pos + 12);

                if (dataSize == 0 || pos + 16 + dataSize > chunk.Length)
                    break;

                if (elemId == targetElemId)
                {
                    blockCount++;
                    sb.AppendLine($"  [{label} chunk {ci}] offset=0x{pos:X}, classIdx=0x{classIdx:X8}, dataSize={dataSize}, hash=0x{hash:X8}");

                    byte[] blockData = new byte[dataSize];
                    Array.Copy(chunk, pos + 16, blockData, 0, (int)dataSize);

                    var strings = ScanStrings(blockData);
                    if (strings.Count > 0)
                    {
                        sb.AppendLine($"    Strings ({strings.Count}):");
                        foreach (var (offset, str) in strings)
                        {
                            sb.AppendLine($"      @0x{offset:X4}: \"{str}\"");
                        }
                    }
                    else
                    {
                        sb.AppendLine("    (no strings found)");
                    }

                    int dumpLen = Math.Min((int)dataSize, 128);
                    sb.Append("    Hex: ");
                    for (int h = 0; h < dumpLen; h++)
                    {
                        sb.Append($"{blockData[h]:X2} ");
                        if ((h + 1) % 32 == 0 && h + 1 < dumpLen)
                            sb.Append("\n         ");
                    }
                    if (dataSize > 128) sb.Append("...");
                    sb.AppendLine();
                }

                pos += 16 + (int)dataSize;
            }
        }

        return blockCount;
    }

    /// <summary>
    /// Scan for length-prefixed UTF-16LE strings: [len:u32][len*2 bytes UTF-16LE].
    /// len must be 3..300, high bytes must be 0x00 or 0x04.
    /// </summary>
    private static List<(int Offset, string Value)> ScanStrings(byte[] data)
    {
        var results = new List<(int, string)>();

        for (int i = 0; i + 4 <= data.Length; i++)
        {
            uint len = BitConverter.ToUInt32(data, i);
            if (len < 3 || len > 300)
                continue;

            int byteLen = (int)(len * 2);
            if (i + 4 + byteLen > data.Length)
                continue;

            bool valid = true;
            for (int j = 0; j < byteLen; j += 2)
            {
                byte highByte = data[i + 4 + j + 1];
                if (highByte != 0x00 && highByte != 0x04)
                {
                    valid = false;
                    break;
                }
            }

            if (!valid)
                continue;

            string str = Encoding.Unicode.GetString(data, i + 4, byteLen);

            bool printable = true;
            foreach (char c in str)
            {
                if (c < 0x20 && c != '\t' && c != '\n' && c != '\r')
                {
                    printable = false;
                    break;
                }
            }

            if (printable)
            {
                results.Add((i, str));
            }
        }

        return results;
    }

    [SkippableFact]
    public void DebugNameExtraction100907()
    {
        string rvtPath = TestFileFixture.FindTestFile("TESTFILE.rvt");
        Assert.True(File.Exists(rvtPath), $"Test file not found: {rvtPath}");

        var reader = new RvtFileReader();
        var (info, state) = reader.ReadWithState(rvtPath);

        const uint TARGET = 100907;
        const uint TARGET_CLASS_IDX = 0xFFFF0200;
        var sb = new StringBuilder();
        sb.AppendLine($"=== Name extraction debug for element {TARGET} ===");
        sb.AppendLine();

        if (state.NativeElementNames.TryGetValue(TARGET, out var nativeName))
            sb.AppendLine($"NativeElementNames[{TARGET}] = \"{nativeName}\"");
        else
            sb.AppendLine($"NativeElementNames does NOT contain {TARGET}");
        sb.AppendLine();

        bool found = false;
        foreach (var chunks in new[] { state.NonRecordChunks, state.RecordChunks })
        {
            string label = chunks == state.NonRecordChunks ? "NonRecord" : "Record";
            for (int ci = 0; ci < chunks.Count; ci++)
            {
                var chunk = chunks[ci];
                int pos = 0;
                while (pos + 16 <= chunk.Length)
                {
                    uint elemId = BitConverter.ToUInt32(chunk, pos);
                    uint hash = BitConverter.ToUInt32(chunk, pos + 4);
                    uint dataSize = BitConverter.ToUInt32(chunk, pos + 8);
                    uint classIdx = BitConverter.ToUInt32(chunk, pos + 12);

                    if (dataSize == 0 || pos + 16 + dataSize > chunk.Length)
                        break;

                    if (elemId == TARGET && classIdx == TARGET_CLASS_IDX)
                    {
                        found = true;
                        sb.AppendLine($"Found block: [{label} chunk {ci}] offset=0x{pos:X}, classIdx=0x{classIdx:X8}, dataSize={dataSize}, hash=0x{hash:X8}");
                        sb.AppendLine();

                        int dataStart = pos + 16;
                        int dataEnd = dataStart + (int)dataSize;

                        sb.AppendLine("--- All length-prefixed UTF-16LE strings (len 3-200) ---");
                        int strCount = 0;
                        for (int j = dataStart; j + 6 <= dataEnd; j++)
                        {
                            uint sLen = BitConverter.ToUInt32(chunk, j);
                            if (sLen < 3 || sLen > 200 || j + 4 + sLen * 2 > dataEnd)
                                continue;

                            bool validHighBytes = true;
                            for (int k = 0; k < (int)sLen && validHighBytes; k++)
                            {
                                byte hi = chunk[j + 4 + k * 2 + 1];
                                if (hi != 0x00 && hi != 0x04)
                                    validHighBytes = false;
                            }
                            if (!validHighBytes) continue;

                            string str = Encoding.Unicode.GetString(chunk, j + 4, (int)sLen * 2);
                            bool printable = true;
                            foreach (char c in str)
                            {
                                if (c < 0x20 && c != '\t' && c != '\n' && c != '\r')
                                { printable = false; break; }
                            }
                            if (!printable) continue;

                            int offsetInBlock = j - dataStart;
                            bool passesIsValid = StringScanner.IsValidElementName(str);

                            bool passesBlockValidation = true;
                            int printableCount = 0;
                            for (int k = 0; k < (int)sLen; k++)
                            {
                                byte lo = chunk[j + 4 + k * 2], hi = chunk[j + 4 + k * 2 + 1];
                                if (hi != 0x00 && hi != 0x04 && hi != 0xF7 && hi != 0x1B && (lo < 0x20 || lo > 0x7E))
                                { passesBlockValidation = false; break; }
                                char c = StringScanner.DecodeUtf16Char(lo, hi);
                                if (c < 0x20 && c != '\t') { passesBlockValidation = false; break; }
                                if (char.IsLetterOrDigit(c) || char.IsPunctuation(c) || char.IsSymbol(c)) printableCount++;
                            }
                            if (passesBlockValidation && printableCount < sLen * 0.6)
                                passesBlockValidation = false;

                            strCount++;
                            sb.AppendLine($"  [{strCount}] @0x{offsetInBlock:X4} (abs 0x{j:X}) len={sLen}");
                            sb.AppendLine($"       text: \"{str}\"");
                            sb.AppendLine($"       IsValidElementName={passesIsValid}, BlockValidation={passesBlockValidation}, printableRatio={printableCount}/{sLen}");
                        }
                        sb.AppendLine($"Total strings found: {strCount}");
                        sb.AppendLine();

                        int dumpLen = Math.Min((int)dataSize, 512);
                        sb.AppendLine($"--- First {dumpLen} bytes of block data (hex) ---");
                        for (int h = 0; h < dumpLen; h++)
                        {
                            sb.Append($"{chunk[dataStart + h]:X2} ");
                            if ((h + 1) % 32 == 0)
                                sb.AppendLine();
                        }
                        if (dataSize > 512) sb.Append("...");
                        sb.AppendLine();
                    }

                    pos += 16 + (int)dataSize;
                }
            }
        }

        if (!found)
            sb.AppendLine($"WARNING: No block found for elem {TARGET} with classIdx=0x{TARGET_CLASS_IDX:X8}");

        string result = sb.ToString();
        File.WriteAllText(TestFiles.Scratch("name_debug_100907.txt"), result, Encoding.UTF8);

        _output.WriteLine(result);
        _output.WriteLine($"Output written to {TestFiles.Scratch("name_debug_100907.txt")}");
    }

    [SkippableFact]
    public void DiagnoseMissingIfcElements()
    {
        string rvtPath = TestFileFixture.FindTestFile("TESTFILE.rvt");
        Assert.True(File.Exists(rvtPath), $"Test file not found: {rvtPath}");

        var reader = new RvtFileReader();
        var (info, state) = reader.ReadWithState(rvtPath);

        var doc = RvtDocument.Open(rvtPath);

        var sb = new StringBuilder();
        sb.AppendLine("=== Missing IFC Elements Diagnostic ===");
        sb.AppendLine($"File: {rvtPath}");
        sb.AppendLine($"Total elements in RevitFileInfo: {info.Elements.Count}");
        sb.AppendLine($"Total elements in RvtDocument: {doc.ElementCount}");
        sb.AppendLine();

        var targets = new (string Label, int[] Ids)[]
        {
            ("Doors", new[] { 289565, 289635 }),
            ("Windows", new[] { 289286, 289487 }),
            ("Wall", new[] { 290112 }),
        };

        foreach (var (label, ids) in targets)
        {
            sb.AppendLine($"========== {label} ==========");
            foreach (int id in ids)
            {
                sb.AppendLine($"--- Element {id} ---");
                var elem = doc.GetElement(id);
                if (elem == null)
                {
                    sb.AppendLine($"  NOT FOUND in RvtDocument");
                    var rawElem = info.Elements.FirstOrDefault(e => e.Id == id);
                    if (rawElem != null)
                        sb.AppendLine($"  Found in raw RevitFileInfo (but not in RvtDocument??)");
                    else
                        sb.AppendLine($"  NOT FOUND in raw RevitFileInfo either");
                    sb.AppendLine();
                    continue;
                }

                sb.AppendLine($"  Id:         {elem.Id}");
                sb.AppendLine($"  Name:       {elem.Name ?? "(null)"}");
                sb.AppendLine($"  Category:   {elem.Category ?? "(null)"}");
                sb.AppendLine($"  CategoryId: {(elem.CategoryId.HasValue ? elem.CategoryId.Value.ToString() : "(null)")}");
                sb.AppendLine($"  TypeId:     {elem.TypeId} (internal class TypeId)");
                sb.AppendLine($"  FamilyName: {elem.FamilyName ?? "(null)"}");
                sb.AppendLine($"  UniqueId:   {elem.UniqueId ?? "(null)"}");

                if (elem.BoundingBox != null && elem.BoundingBox.Length == 6)
                {
                    var bb = elem.BoundingBox;
                    sb.AppendLine($"  BoundingBox: Min({bb[0]:F3}, {bb[1]:F3}, {bb[2]:F3}) Max({bb[3]:F3}, {bb[4]:F3}, {bb[5]:F3})");
                }
                else
                {
                    sb.AppendLine($"  BoundingBox: {(elem.BoundingBox == null ? "NULL" : $"length={elem.BoundingBox.Length}")}");
                }

                if (elem.Geometry != null)
                {
                    var geo = elem.Geometry;
                    sb.AppendLine($"  Geometry:   EXISTS");
                    sb.AppendLine($"    GStyleElemId: {geo.GStyleElemId}");
                    sb.AppendLine($"    Flags:        0x{geo.Flags:X8}");
                    sb.AppendLine($"    Faces:        {geo.Faces.Count}");
                    sb.AppendLine($"    Edges:        {geo.Edges?.Count ?? 0}");
                    if (geo.BoundingBox != null && geo.BoundingBox.Length == 6)
                    {
                        var gbb = geo.BoundingBox;
                        sb.AppendLine($"    GeoBBox:      Min({gbb[0]:F3}, {gbb[1]:F3}, {gbb[2]:F3}) Max({gbb[3]:F3}, {gbb[4]:F3}, {gbb[5]:F3})");
                    }
                    else
                    {
                        sb.AppendLine($"    GeoBBox:      {(geo.BoundingBox == null ? "NULL" : $"length={geo.BoundingBox.Length}")}");
                    }
                }
                else
                {
                    sb.AppendLine($"  Geometry:   NULL");
                }

                sb.AppendLine($"  Parameters: {elem.Parameters.Count} total");
                int paramIdx = 0;
                foreach (var p in elem.Parameters.Take(10))
                {
                    paramIdx++;
                    string pName = p.Definition?.Name ?? "(no def)";
                    string pVal = p.Value?.ToString() ?? "(null)";
                    if (pVal.Length > 80) pVal = pVal.Substring(0, 80) + "...";
                    sb.AppendLine($"    [{paramIdx}] {pName} = {pVal} (StorageType={p.StorageType})");
                }
                if (elem.Parameters.Count > 10)
                    sb.AppendLine($"    ... and {elem.Parameters.Count - 10} more");

                int refCount = elem.References?.Count ?? 0;
                sb.AppendLine($"  References: {refCount}");
                if (elem.References != null)
                {
                    foreach (int refId in elem.References.Take(5))
                    {
                        var refElem = doc.GetElement(refId);
                        string refDesc = refElem != null
                            ? $"{refElem.Name ?? "?"} (Cat={refElem.Category ?? "?"}, TypeId={refElem.TypeId})"
                            : "NOT FOUND";
                        sb.AppendLine($"    -> {refId}: {refDesc}");
                    }
                    if (refCount > 5)
                        sb.AppendLine($"    ... and {refCount - 5} more");
                }

                var typeParam = elem.LookupParameter("ELEM_TYPE_PARAM");
                if (typeParam != null && typeParam.Value is int typeElemId && typeElemId > 0)
                {
                    sb.AppendLine($"  ELEM_TYPE_PARAM: {typeElemId}");
                    var typeElem = doc.GetElement(typeElemId);
                    if (typeElem != null)
                    {
                        sb.AppendLine($"    Type element Name:       {typeElem.Name ?? "(null)"}");
                        sb.AppendLine($"    Type element Category:   {typeElem.Category ?? "(null)"}");
                        sb.AppendLine($"    Type element CategoryId: {(typeElem.CategoryId.HasValue ? typeElem.CategoryId.Value.ToString() : "(null)")}");
                        sb.AppendLine($"    Type element Params:     {typeElem.Parameters.Count}");
                        sb.AppendLine($"    Type element Geometry:   {(typeElem.Geometry != null ? $"YES (faces={typeElem.Geometry.Faces.Count})" : "NULL")}");
                        sb.AppendLine($"    Type element BBox:       {(typeElem.BoundingBox != null ? "YES" : "NULL")}");
                    }
                    else
                    {
                        sb.AppendLine($"    Type element NOT FOUND in document");
                    }
                }
                else
                {
                    sb.AppendLine($"  ELEM_TYPE_PARAM: not found or invalid");
                    int ftId = elem.FamilyTypeId;
                    if (ftId > 0)
                    {
                        sb.AppendLine($"  FamilyTypeId (fallback): {ftId}");
                        var ftElem = doc.GetElement(ftId);
                        if (ftElem != null)
                        {
                            sb.AppendLine($"    -> Name={ftElem.Name ?? "(null)"}, Geometry={ftElem.Geometry != null}");
                        }
                    }
                }

                sb.AppendLine();
            }
        }

        sb.AppendLine("========== Category Scan ==========");
        const int CAT_DOORS = -2000023;   // OST_Doors
        const int CAT_WINDOWS = -2000014; // OST_Windows

        var doorElems = doc.Elements.Where(e => e.CategoryId == CAT_DOORS).ToList();
        sb.AppendLine($"Elements with CategoryId={CAT_DOORS} (OST_Doors): {doorElems.Count}");
        foreach (var d in doorElems.Take(20))
        {
            sb.AppendLine($"  {d.Id}: Name={d.Name ?? "(null)"}, TypeId={d.TypeId}, " +
                          $"Geom={d.Geometry != null}, BBox={d.BoundingBox != null}, " +
                          $"Params={d.Parameters.Count}, Refs={d.References?.Count ?? 0}");
        }
        if (doorElems.Count > 20)
            sb.AppendLine($"  ... and {doorElems.Count - 20} more");
        sb.AppendLine();

        var winElems = doc.Elements.Where(e => e.CategoryId == CAT_WINDOWS).ToList();
        sb.AppendLine($"Elements with CategoryId={CAT_WINDOWS} (OST_Windows): {winElems.Count}");
        foreach (var w in winElems.Take(20))
        {
            sb.AppendLine($"  {w.Id}: Name={w.Name ?? "(null)"}, TypeId={w.TypeId}, " +
                          $"Geom={w.Geometry != null}, BBox={w.BoundingBox != null}, " +
                          $"Params={w.Parameters.Count}, Refs={w.References?.Count ?? 0}");
        }
        if (winElems.Count > 20)
            sb.AppendLine($"  ... and {winElems.Count - 20} more");
        sb.AppendLine();

        const int CAT_32 = -2000032;
        var cat32Elems = doc.Elements.Where(e => e.CategoryId == CAT_32).ToList();
        sb.AppendLine($"Elements with CategoryId={CAT_32}: {cat32Elems.Count}");
        foreach (var c in cat32Elems.Take(10))
        {
            sb.AppendLine($"  {c.Id}: Name={c.Name ?? "(null)"}, Category={c.Category ?? "(null)"}");
        }
        if (cat32Elems.Count > 10)
            sb.AppendLine($"  ... and {cat32Elems.Count - 10} more");

        string result = sb.ToString();
        File.WriteAllText(TestFiles.Scratch("missing_elements_diag.txt"), result, Encoding.UTF8);

        _output.WriteLine(result);
        _output.WriteLine($"Output written to {TestFiles.Scratch("missing_elements_diag.txt")}");
    }

    [SkippableFact]
    public void ScanAllPartitions()
    {
        string rvtPath = TestFileFixture.FindTestFile("TESTFILE.rvt");
        Assert.True(File.Exists(rvtPath), $"Test file not found: {rvtPath}");

        var sb = new StringBuilder();
        sb.AppendLine("=== OLE2 Partition Scan for Missing Elements ===");
        sb.AppendLine($"File: {rvtPath}");
        sb.AppendLine();

        // Target element to search for: 289565 = 0x00046B1D
        uint targetElemId = 289565;
        byte[] targetBytes = BitConverter.GetBytes(targetElemId);
        sb.AppendLine($"Searching for element {targetElemId} (bytes: {BitConverter.ToString(targetBytes)})");
        sb.AppendLine();

        using var rootStorage = OpenMcdf.RootStorage.OpenRead(rvtPath);

        sb.AppendLine("========== Full OLE2 Structure ==========");
        EnumerateStorageRecursive(rootStorage, "", sb);
        sb.AppendLine();

        sb.AppendLine("========== Partition Stream Scan ==========");
        try
        {
            var partitionsStorage = rootStorage.OpenStorage("Partitions");
            foreach (var entry in partitionsStorage.EnumerateEntries())
            {
                if (entry.Type == OpenMcdf.EntryType.Stream)
                {
                    sb.AppendLine($"--- Partitions/{entry.Name} (size={entry.Length}) ---");
                    ScanStreamForElement(partitionsStorage, entry.Name, targetBytes, targetElemId, sb);
                }
                else if (entry.Type == OpenMcdf.EntryType.Storage)
                {
                    sb.AppendLine($"--- Partitions/{entry.Name}/ (storage) ---");
                    var subStorage = partitionsStorage.OpenStorage(entry.Name);
                    foreach (var subEntry in subStorage.EnumerateEntries())
                    {
                        if (subEntry.Type == OpenMcdf.EntryType.Stream)
                        {
                            sb.AppendLine($"  --- Partitions/{entry.Name}/{subEntry.Name} (size={subEntry.Length}) ---");
                            ScanStreamForElement(subStorage, subEntry.Name, targetBytes, targetElemId, sb);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Error reading Partitions: {ex.Message}");
        }
        sb.AppendLine();

        sb.AppendLine("========== Global/Latest Scan ==========");
        try
        {
            var globalStorage = rootStorage.OpenStorage("Global");
            foreach (var entry in globalStorage.EnumerateEntries())
            {
                if (entry.Type == OpenMcdf.EntryType.Stream && entry.Name == "Latest")
                {
                    sb.AppendLine($"Global/Latest (size={entry.Length})");
                    ScanStreamForElement(globalStorage, "Latest", targetBytes, targetElemId, sb);
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Error reading Global/Latest: {ex.Message}");
        }
        sb.AppendLine();

        sb.AppendLine("========== Formats/Latest Scan ==========");
        try
        {
            var formatsStorage = rootStorage.OpenStorage("Formats");
            foreach (var entry in formatsStorage.EnumerateEntries())
            {
                if (entry.Type == OpenMcdf.EntryType.Stream && entry.Name == "Latest")
                {
                    sb.AppendLine($"Formats/Latest (size={entry.Length})");
                    ScanStreamForElement(formatsStorage, "Latest", targetBytes, targetElemId, sb);
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Error reading Formats/Latest: {ex.Message}");
        }

        string result = sb.ToString();
        File.WriteAllText(TestFiles.Scratch("partition_scan.txt"), result, Encoding.UTF8);

        _output.WriteLine(result);
        _output.WriteLine($"Output written to {TestFiles.Scratch("partition_scan.txt")}");
    }

    private static void EnumerateStorageRecursive(OpenMcdf.Storage storage, string path, StringBuilder sb)
    {
        foreach (var entry in storage.EnumerateEntries())
        {
            string fullPath = string.IsNullOrEmpty(path) ? entry.Name : $"{path}/{entry.Name}";
            if (entry.Type == OpenMcdf.EntryType.Stream)
            {
                sb.AppendLine($"  [Stream] {fullPath}  (size={entry.Length})");
            }
            else if (entry.Type == OpenMcdf.EntryType.Storage)
            {
                sb.AppendLine($"  [Storage] {fullPath}/");
                var sub = storage.OpenStorage(entry.Name);
                EnumerateStorageRecursive(sub, fullPath, sb);
            }
        }
    }

    private static void ScanStreamForElement(OpenMcdf.Storage storage, string streamName,
        byte[] targetBytes, uint targetElemId, StringBuilder sb)
    {
        try
        {
            byte[] rawData;
            using (var stream = storage.OpenStream(streamName))
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                rawData = ms.ToArray();
            }

            var rawHits = ScanBytesForPattern(rawData, targetBytes);
            sb.AppendLine($"  Raw bytes: {rawData.Length}, target hits in raw: {rawHits.Count}");
            foreach (int off in rawHits.Take(5))
                sb.AppendLine($"    Raw hit @0x{off:X}");

            var gzipMarkers = new List<int>();
            for (int i = 0; i <= rawData.Length - 3; i++)
            {
                if (rawData[i] == 0x1F && rawData[i + 1] == 0x8B && rawData[i + 2] == 0x08)
                    gzipMarkers.Add(i);
            }
            sb.AppendLine($"  GZIP markers found: {gzipMarkers.Count}");

            if (gzipMarkers.Count == 0)
                return;

            int successCount = 0;
            int failCount = 0;
            int chunksWithTarget = 0;
            long totalDecompressed = 0;

            for (int gi = 0; gi < gzipMarkers.Count; gi++)
            {
                int start = gzipMarkers[gi];
                int end = (gi + 1 < gzipMarkers.Count) ? gzipMarkers[gi + 1] : rawData.Length;
                int chunkLen = end - start;

                try
                {
                    using var chunkMs = new MemoryStream(rawData, start, chunkLen);
                    using var gzStream = new System.IO.Compression.GZipStream(chunkMs, System.IO.Compression.CompressionMode.Decompress);
                    using var outMs = new MemoryStream();
                    gzStream.CopyTo(outMs);
                    byte[] decompressed = outMs.ToArray();

                    successCount++;
                    totalDecompressed += decompressed.Length;

                    var hits = ScanBytesForPattern(decompressed, targetBytes);
                    if (hits.Count > 0)
                    {
                        chunksWithTarget++;
                        sb.AppendLine($"  *** FOUND in GZIP chunk {gi} (marker @0x{start:X}, decompressed={decompressed.Length}): {hits.Count} hits");
                        foreach (int off in hits.Take(10))
                        {
                            sb.Append($"    @0x{off:X}: ");
                            int ctxStart = Math.Max(0, off - 8);
                            int ctxEnd = Math.Min(decompressed.Length, off + 20);
                            for (int b = ctxStart; b < ctxEnd; b++)
                            {
                                if (b == off) sb.Append("[");
                                sb.Append($"{decompressed[b]:X2} ");
                                if (b == off + 3) sb.Append("] ");
                            }
                            sb.AppendLine();
                        }
                    }
                }
                catch
                {
                    failCount++;
                }
            }

            sb.AppendLine($"  GZIP decompress: {successCount} success, {failCount} fail, {totalDecompressed} bytes total");
            sb.AppendLine($"  Chunks containing element {targetElemId}: {chunksWithTarget}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  Error: {ex.Message}");
        }
    }

    private static List<int> ScanBytesForPattern(byte[] data, byte[] pattern)
    {
        var hits = new List<int>();
        for (int i = 0; i <= data.Length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j]) { match = false; break; }
            }
            if (match) hits.Add(i);
        }
        return hits;
    }

    [SkippableFact]
    public void InvestigateMissingRecords()
    {
        string rvtPath = TestFileFixture.FindTestFile("TESTFILE.rvt");
        Assert.True(File.Exists(rvtPath), $"Test file not found: {rvtPath}");

        var reader = new RvtFileReader();
        var (info, state) = reader.ReadWithState(rvtPath);

        var sb = new StringBuilder();
        sb.AppendLine("=== Missing Records Investigation ===");
        sb.AppendLine($"File: {rvtPath}");
        sb.AppendLine($"ElemTable entries: {state.ElemTable.Count}");
        sb.AppendLine($"Records: {state.Records.Count}");
        sb.AppendLine($"AllKnownIds: {state.AllKnownIds.Count}");
        sb.AppendLine($"RecordChunks: {state.RecordChunks.Count}");
        sb.AppendLine($"NonRecordChunks: {state.NonRecordChunks.Count}");
        sb.AppendLine($"Chunks: {state.Chunks.Count}");
        sb.AppendLine();

        uint[] targetIds = { 289565, 289635, 289286, 289487, 290112 };

        foreach (uint eid in targetIds)
        {
            sb.AppendLine($"============ Element {eid} (0x{eid:X8}) ============");

            if (state.Records.TryGetValue(eid, out var rec))
            {
                sb.AppendLine($"  Records: FOUND");
                sb.AppendLine($"    Subclass: 0x{rec.Subclass:X4}");
                sb.AppendLine($"    Body length: {rec.Body.Length}");
                sb.AppendLine($"    References: {rec.References.Count} -> [{string.Join(", ", rec.References.Take(10))}]");
                sb.AppendLine($"    BipParams: {rec.BipParams.Count}");
                foreach (var bp in rec.BipParams.Take(10))
                    sb.AppendLine($"      BIP {bp.BipId} ({bp.BipName ?? "?"}) = {bp.Value} (FT={bp.FieldType})");
                sb.AppendLine($"    Geometry: {(rec.Geometry != null ? $"[{string.Join(", ", rec.Geometry.Select(d => d.ToString("F3")))}]" : "NULL")}");
                sb.AppendLine($"    CategoryId: {(rec.CategoryId.HasValue ? rec.CategoryId.Value.ToString() : "NULL")}");
            }
            else
            {
                sb.AppendLine($"  Records: NOT FOUND");
            }

            byte[] eidBytes = BitConverter.GetBytes(eid);
            sb.AppendLine();
            sb.AppendLine($"  --- Byte scan for {eid} (bytes: {BitConverter.ToString(eidBytes)}) ---");

            void ScanChunkList(IReadOnlyList<byte[]> chunks, string label)
            {
                int totalHits = 0;
                for (int ci = 0; ci < chunks.Count; ci++)
                {
                    var chunk = chunks[ci];
                    var offsets = new List<int>();
                    for (int i = 0; i <= chunk.Length - 4; i++)
                    {
                        if (chunk[i] == eidBytes[0] && chunk[i + 1] == eidBytes[1]
                            && chunk[i + 2] == eidBytes[2] && chunk[i + 3] == eidBytes[3])
                        {
                            offsets.Add(i);
                        }
                    }
                    if (offsets.Count > 0)
                    {
                        totalHits += offsets.Count;
                        sb.AppendLine($"    {label}[{ci}] ({chunk.Length} bytes): {offsets.Count} hits at offsets [{string.Join(", ", offsets.Select(o => $"0x{o:X}"))}]");
                        foreach (int off in offsets.Take(3))
                        {
                            int ctxStart = Math.Max(0, off - 8);
                            int ctxEnd = Math.Min(chunk.Length, off + 20);
                            sb.Append($"      context @0x{off:X}: ");
                            for (int b = ctxStart; b < ctxEnd; b++)
                            {
                                if (b == off) sb.Append("[");
                                sb.Append($"{chunk[b]:X2} ");
                                if (b == off + 3) sb.Append("] ");
                            }
                            sb.AppendLine();
                        }
                    }
                }
                if (totalHits == 0)
                    sb.AppendLine($"    {label}: NO HITS in any chunk");
                else
                    sb.AppendLine($"    {label}: {totalHits} total hits");
            }

            ScanChunkList(state.RecordChunks, "RecordChunks");
            ScanChunkList(state.NonRecordChunks, "NonRecordChunks");
            ScanChunkList(state.Chunks, "Chunks");

            sb.AppendLine();
            sb.AppendLine($"  --- Block scan in NonRecordChunks ---");
            int blockCount = 0;
            for (int ci = 0; ci < state.NonRecordChunks.Count; ci++)
            {
                var chunk = state.NonRecordChunks[ci];
                int pos = 0;
                while (pos + 16 <= chunk.Length)
                {
                    uint blkElemId = BitConverter.ToUInt32(chunk, pos);
                    uint hash = BitConverter.ToUInt32(chunk, pos + 4);
                    uint dataSize = BitConverter.ToUInt32(chunk, pos + 8);
                    uint classIdx = BitConverter.ToUInt32(chunk, pos + 12);

                    if (dataSize == 0 || pos + 16 + dataSize > chunk.Length)
                        break;

                    if (blkElemId == eid)
                    {
                        blockCount++;
                        sb.AppendLine($"    Block in NonRecordChunks[{ci}] @0x{pos:X}: classIdx=0x{classIdx:X8}, dataSize={dataSize}, hash=0x{hash:X8}");
                        int dumpLen = Math.Min((int)dataSize, 64);
                        sb.Append("      Hex: ");
                        for (int h = 0; h < dumpLen; h++)
                            sb.Append($"{chunk[pos + 16 + h]:X2} ");
                        if (dataSize > 64) sb.Append("...");
                        sb.AppendLine();
                    }

                    pos += 16 + (int)dataSize;
                }
            }
            if (blockCount == 0)
                sb.AppendLine($"    No blocks found for {eid} in NonRecordChunks");
            else
                sb.AppendLine($"    {blockCount} blocks found");

            sb.AppendLine($"  --- Block scan in RecordChunks ---");
            int recBlockCount = 0;
            for (int ci = 0; ci < state.RecordChunks.Count; ci++)
            {
                var chunk = state.RecordChunks[ci];
                int pos = 0;
                while (pos + 16 <= chunk.Length)
                {
                    uint blkElemId = BitConverter.ToUInt32(chunk, pos);
                    uint hash = BitConverter.ToUInt32(chunk, pos + 4);
                    uint dataSize = BitConverter.ToUInt32(chunk, pos + 8);
                    uint classIdx = BitConverter.ToUInt32(chunk, pos + 12);

                    if (dataSize == 0 || pos + 16 + dataSize > chunk.Length)
                        break;

                    if (blkElemId == eid)
                    {
                        recBlockCount++;
                        sb.AppendLine($"    Block in RecordChunks[{ci}] @0x{pos:X}: classIdx=0x{classIdx:X8}, dataSize={dataSize}, hash=0x{hash:X8}");
                        int dumpLen = Math.Min((int)dataSize, 64);
                        sb.Append("      Hex: ");
                        for (int h = 0; h < dumpLen; h++)
                            sb.Append($"{chunk[pos + 16 + h]:X2} ");
                        if (dataSize > 64) sb.Append("...");
                        sb.AppendLine();
                    }

                    pos += 16 + (int)dataSize;
                }
            }
            if (recBlockCount == 0)
                sb.AppendLine($"    No blocks found for {eid} in RecordChunks");
            else
                sb.AppendLine($"    {recBlockCount} blocks found");

            if (state.ElemTable.TryGetValue(eid, out var typeId))
                sb.AppendLine($"  ElemTable: FOUND, typeId={typeId} (0x{typeId:X})");
            else
                sb.AppendLine($"  ElemTable: NOT FOUND");

            sb.AppendLine($"  AllKnownIds: {(state.AllKnownIds.Contains(eid) ? "YES" : "NO")}");

            if (state.ElemStrings.TryGetValue(eid, out var strings))
                sb.AppendLine($"  ElemStrings: {strings.Count} strings -> [{string.Join(", ", strings.Take(5).Select(s => $"\"{s}\""))}]");
            else
                sb.AppendLine($"  ElemStrings: NONE");

            if (state.ElemScanStrings.TryGetValue(eid, out var scanStrings))
                sb.AppendLine($"  ElemScanStrings: {scanStrings.Count} strings -> [{string.Join(", ", scanStrings.Take(5).Select(s => $"\"{s}\""))}]");
            else
                sb.AppendLine($"  ElemScanStrings: NONE");

            if (state.ElemBipDoubles.TryGetValue(eid, out var bipDoubles))
            {
                sb.AppendLine($"  ElemBipDoubles: {bipDoubles.Count} entries");
                foreach (var (k, v) in bipDoubles.Take(10))
                    sb.AppendLine($"    BIP {k} = {v:F6}");
            }
            else
                sb.AppendLine($"  ElemBipDoubles: NONE");

            if (state.ElemBaseFields.TryGetValue(eid, out var baseFields))
            {
                sb.AppendLine($"  ElemBaseFields: AssocLevel={baseFields.AssocLevelId}, FamId={baseFields.FamId}, " +
                              $"CreatedPhase={baseFields.CreatedPhaseId}, DemolishedPhase={baseFields.DemolishedPhaseId}, " +
                              $"DesignOption={baseFields.BaseDesignOptionId}");
            }
            else
                sb.AppendLine($"  ElemBaseFields: NONE");

            if (state.GrepBlocks.TryGetValue(eid, out var grep))
                sb.AppendLine($"  GrepBlocks: FOUND (GStyleElemId={grep.GStyleElemId}, Faces={grep.Faces.Count}, BBox={grep.BoundingBox != null})");
            else
                sb.AppendLine($"  GrepBlocks: NONE");

            if (state.NativeElementNames.TryGetValue(eid, out var nName))
                sb.AppendLine($"  NativeElementNames: \"{nName}\"");
            else
                sb.AppendLine($"  NativeElementNames: NONE");

            if (state.BlockOnlyIds.Contains(eid))
                sb.AppendLine($"  BlockOnlyIds: YES (block-only element)");

            sb.AppendLine();
        }

        sb.AppendLine("============ Orphan ElemTable Entries ============");
        var orphans = state.ElemTable.Keys
            .Where(k => !state.Records.ContainsKey(k))
            .OrderBy(k => k)
            .ToList();
        sb.AppendLine($"ElemTable entries with no Record: {orphans.Count} / {state.ElemTable.Count}");
        sb.AppendLine("First 20 orphan IDs:");
        foreach (var oid in orphans.Take(20))
        {
            uint oTypeId = state.ElemTable[oid];
            bool hasBlocks = state.BlockOnlyIds.Contains(oid)
                || state.ElemStrings.ContainsKey(oid)
                || state.ElemBipDoubles.ContainsKey(oid)
                || state.ElemBaseFields.ContainsKey(oid);
            sb.AppendLine($"  {oid} (0x{oid:X}): typeId={oTypeId}, hasBlockData={hasBlocks}");
        }

        string result = sb.ToString();
        File.WriteAllText(TestFiles.Scratch("missing_records_diag.txt"), result, Encoding.UTF8);

        _output.WriteLine(result);
        _output.WriteLine($"Output written to {TestFiles.Scratch("missing_records_diag.txt")}");
    }
}

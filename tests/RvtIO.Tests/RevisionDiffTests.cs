using System.Text.Json;
using RvtIO.BuiltIns;
using RvtIO.Cli;
using RvtIO.Diff;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

public class RevisionDiffTests
{
    private static readonly Guid ProjectGuidA = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid ProjectGuidB = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static Parameter MakeParam(BuiltInParameter bip, StorageType storage, object value, string? unitSymbol = null, double? factor = null) => new()
    {
        Id = (int)bip,
        Definition = new Definition { Id = (int)bip, BuiltInParameter = bip.ToString(), Name = bip.ToString() },
        StorageType = storage,
        Value = value,
        UnitSymbol = unitSymbol,
        ConversionFactor = factor,
    };

    private static Element MakeWall(int id, string uid, int typeId, double x2, double heightFt = 10.0, string comments = "initial", string unitSymbol = "mm", double? factor = 304.8)
    {
        return new Element
        {
            Id = id,
            UniqueId = uid,
            CategoryId = (int)BuiltInCategory.OST_Walls,
            Category = "OST_Walls",
            Name = "Wall",
            TypeId = typeId,
            Location = new LocationCurve
            {
                CurveType = "Line",
                StartPoint = [0, 0, 0],
                EndPoint = [x2, 0, 0],
                Direction = [1, 0, 0],
                Length = x2,
            },
            BoundingBox = [0, -0.5, 0, x2, 0.5, heightFt],
            Geometry = new GeometryData
            {
                BoundingBox = [0, -0.5, 0, x2, 0.5, heightFt],
                Faces =
                [
                    new GeoFace([0, -0.5, 0], [0, -1, 0]) { Area = x2 * heightFt },
                    new GeoFace([0, 0.5, 0], [0, 1, 0]) { Area = x2 * heightFt },
                ],
                Solids =
                [
                    new GeoSolid { ProfileWidth = x2, ProfileDepth = 1.0, ExtrusionHeight = heightFt },
                ],
            },
            Parameters =
            [
                MakeParam(BuiltInParameter.ELEM_TYPE_PARAM, StorageType.ElementId, typeId),
                MakeParam(BuiltInParameter.WALL_USER_HEIGHT_PARAM, StorageType.Double, heightFt, unitSymbol, factor),
                MakeParam(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, StorageType.String, comments),
            ],
        };
    }

    private static Element MakeWallType(int id, string uid, string name, double thicknessFt) => new()
    {
        Id = id,
        UniqueId = uid,
        CategoryId = (int)BuiltInCategory.OST_Walls,
        Category = "OST_Walls",
        Name = name,
        FamilyName = "Basic Wall",
        NativeIsSymbol = true,
        Layers =
        [
            new MaterialLayer { LayerIndex = 0, ThicknessMm = thicknessFt * 304.8, Function = "Structure", MaterialName = "Concrete" },
        ],
        Parameters =
        [
            MakeParam(BuiltInParameter.ALL_MODEL_TYPE_NAME, StorageType.String, name),
            MakeParam(BuiltInParameter.WALL_ATTR_WIDTH_PARAM, StorageType.Double, thicknessFt),
        ],
    };

    private static RvtDocument BuildDoc(Guid? docGuid, List<Element> elements, string version = "2023",
        TransmissionInfo? transmission = null, bool exact = true, bool wideNotRewritten = false, string? streamVer = null)
    {
        var info = new RevitFileInfo
        {
            SerializedDocumentGuid = docGuid,
            ElemTableIsExact = exact,
            WideLayoutNotRewritten = wideNotRewritten,
            GeometryUndecoded = wideNotRewritten,
            Metadata = new FileMetadata { RevitVersion = version, StreamVersion = streamVer },
            Elements = elements,
            TransmissionData = transmission,
        };
        return RvtDocument.FromInfo(info);
    }

    private static List<double[]> Square(double x, double y, double size) =>
        [[x, y, 0], [x + size, y, 0], [x + size, y + size, 0], [x, y + size, 0]];

    private static GeoFace PlanFace() => new()
    {
        Area = 96, Origin = [0, 0, 0], Normal = [0, 0, 1], XVector = [1, 0, 0], YVector = [0, 1, 0],
        Envelope = [0, 0, 10, 10], OuterVertices = Square(0, 0, 10),
    };

    private static RvtDocument GeometryDoc(GeometryData geometry, double offsetX = 0) => BuildDoc(ProjectGuidA,
    [
        new Element
        {
            Id = 100, UniqueId = "uid-face-owner", BoundingBox = [offsetX, 0, 0, offsetX + 10, 10, 0], Geometry = geometry,
        },
    ]);

    [Theory]
    [InlineData("source-loops")]
    [InlineData("inner-vertices")]
    [InlineData("inner-envelopes")]
    [InlineData("unprojected-envelopes")]
    public void MovingAnEqualSizedBrepCutoutIsAGeometryChange(string representation)
    {
        RvtDocument Revision(double x)
        {
            var face = PlanFace();
            switch (representation)
            {
                case "source-loops": face.SourceLoops = [face.OuterVertices!, Square(x, 2, 2)]; break;
                case "inner-vertices": face.InnerVertices = [Square(x, 2, 2)]; break;
                case "unprojected-envelopes":
                    face.XVector = face.YVector = new double[3];
                    goto case "inner-envelopes";
                case "inner-envelopes": face.InnerLoops = [[x, 2, x + 2, 4]]; break;
            }
            return GeometryDoc(new GeometryData { Faces = [face], BrepSolids = [new GeoBrepSolid { FaceIndices = [0] }] });
        }

        var diff = RvtRevisionDiffer.Compare(Revision(2), Revision(6));

        var geometry = Assert.Single(diff.Modified).GeometryChange!;
        Assert.Equal(RvtDiffAvailability.Confirmed, geometry.Availability);
        Assert.Equal(["faces"], geometry.ChangedAspects);
        Assert.Equal(96, geometry.Old.TotalFaceAreaSquareFeet);
        Assert.Equal(geometry.Old.TotalFaceAreaSquareFeet, geometry.New.TotalFaceAreaSquareFeet);
    }

    [Fact]
    public void ReorderedFacesAndCyclicReversedContoursProduceNoChange()
    {
        var horizontal = PlanFace();
        horizontal.InnerVertices = [Square(2, 2, 2), Square(6, 6, 2)];
        var reordered = PlanFace();
        reordered.OuterVertices = horizontal.OuterVertices!.Skip(1).Append(horizontal.OuterVertices![0]).Reverse().ToList();
        reordered.InnerVertices = horizontal.InnerVertices.Select(r => r.AsEnumerable().Reverse().ToList()).Reverse().ToList();
        var vertical = new GeoFace { Area = 96, Normal = [1, 0, 0] };

        var diff = RvtRevisionDiffer.Compare(GeometryDoc(new GeometryData { Faces = [horizontal, vertical] }),
            GeometryDoc(new GeometryData { Faces = [vertical, reordered] }));

        Assert.Equal(1, diff.UnchangedCount);
        Assert.Empty(diff.Modified);
    }

    [Fact]
    public void TranslatingTheWholeFaceDoesNotChangeItsContourGeometry()
    {
        RvtDocument Revision(double x)
        {
            var face = PlanFace();
            face.Origin = [x, 0, 0];
            face.OuterVertices = Square(x, 0, 10);
            face.InnerVertices = [Square(x + 2, 2, 2)];
            return GeometryDoc(new GeometryData { Faces = [face],
                Transform = [1, 0, 0, x, 0, 1, 0, 0, 0, 0, 1, 0] }, x);
        }

        var change = Assert.Single(RvtRevisionDiffer.Compare(Revision(0), Revision(5)).Modified);

        Assert.Equal(5, change.PlacementChange!.TranslationFeet.X);
        Assert.Null(change.GeometryChange);
    }

    [Fact]
    public void FaceMatchingCanReviseAnEarlierPairWithinTolerance()
    {
        RvtDocument Revision(params double[] origins) => GeometryDoc(new GeometryData
        {
            Faces = origins.Select(x => new GeoFace { Origin = [x, 0, 0], Normal = [0, 0, 1], Area = 10 }).ToList(),
        });

        var diff = RvtRevisionDiffer.Compare(Revision(0.000075, 0), Revision(0.00005, 0.00016));

        Assert.Equal(1, diff.UnchangedCount);
    }

    [Fact]
    public void MovingAnEqualSizedExtrusionOpeningIsAGeometryChange()
    {
        RvtDocument Revision(double x) => GeometryDoc(new GeometryData
        {
            Solids = [new GeoSolid { ProfileWidth = 10, ProfileDepth = 1, ExtrusionHeight = 10,
                Openings = [new GeoOpening { Position = [x, 0, 2], Width = 2, Height = 2 }] }],
        });

        var diff = RvtRevisionDiffer.Compare(Revision(2), Revision(6));

        Assert.Equal(["solids"], Assert.Single(diff.Modified).GeometryChange!.ChangedAspects);
    }

    [Fact]
    public void ChangedFaceIsDetectedAboveFiveHundredFaces()
    {
        RvtDocument Revision(bool changed) => GeometryDoc(new GeometryData
        {
            Faces = Enumerable.Range(0, 501).Select(i => new GeoFace
            {
                Origin = [i, 0, 0], Normal = i == 500 && changed ? [0, 1, 0] : [0, 0, 1], Area = 1,
            }).ToList(),
        });

        var diff = RvtRevisionDiffer.Compare(Revision(false), Revision(true));

        Assert.Equal(["faces"], Assert.Single(diff.Modified).GeometryChange!.ChangedAspects);
    }

    [Fact]
    public void EqualDecodedFacesDoNotConfirmIncompleteGeometry()
    {
        var left = GeometryDoc(new GeometryData { Faces = [PlanFace()] });
        var right = GeometryDoc(new GeometryData { Faces = [PlanFace()], UntriangulatedFaces = 1 });

        var diff = RvtRevisionDiffer.Compare(left, right);

        Assert.Equal(0, diff.UnchangedCount);
        Assert.False(diff.HasSemanticChanges);
        var result = Assert.Single(diff.Modified);
        Assert.Contains("geometry", result.UnavailableAspects);
        Assert.Equal(RvtDiffAvailability.Partial, result.GeometryChange!.Availability);
        Assert.Empty(result.GeometryChange.ChangedAspects);
    }

    [Fact]
    public void RedactedDiffHidesEmailInReadDiagnostics()
    {
        var folder = Directory.CreateTempSubdirectory("rvtio-diff-diagnostic-");
        try
        {
            const string email = "probe.author@example.test";
            string path = Path.Combine(folder.FullName, email + ".rvt");
            File.WriteAllText(path, "Invalid compound file");
            using var error = new StringWriter();

            int exit = DiffCommand.Run([path, path, "--redact", "--json"], TextWriter.Null, error);

            Assert.Equal(RvtExitCodes.InputError, exit);
            Assert.Contains("<redacted>", error.ToString());
            Assert.DoesNotContain(email, error.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally { folder.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("--linear-tol", "NaN")]
    [InlineData("--angle-tol", "Infinity")]
    public void DiffRejectsNonFiniteTolerances(string option, string value)
    {
        using var error = new StringWriter();
        Assert.Equal(RvtExitCodes.Usage, DiffCommand.Run(["left.rvt", "right.rvt", option, value], TextWriter.Null, error));
        Assert.Contains("finite", error.ToString());
    }

    [Fact]
    public void ResavedModelWithRenumberedIdsReorderedFacesAndDisplayUnitUpgrade_ProducesEmptyDiff()
    {
        var left = BuildDoc(ProjectGuidA, [
            MakeWallType(10, "uid-type-1", "Generic 200", 0.656168),
            MakeWall(100, "uid-wall-1", 10, 20.0, 10.0, "exterior", "mm", 304.8),
        ], version: "2022");

        // Upgraded save: Revit 2024, numeric IDs changed (10->5010, 100->9100), faces reversed, tiny float noise < 1e-6, display unit m instead of mm.
        var wallRight = MakeWall(9100, "uid-wall-1", 5010, 20.0 + 1e-6, 10.0 + 1e-7, "exterior", "m", 0.3048);
        wallRight.Geometry!.Faces.Reverse();
        var right = BuildDoc(ProjectGuidA, [
            wallRight,
            MakeWallType(5010, "uid-type-1", "Generic 200", 0.656168),
        ], version: "2024");

        var diff = RvtRevisionDiffer.Compare(left, right);

        Assert.Equal(RvtRevisionRelation.Related, diff.Relation);
        Assert.True(diff.VersionChanged);
        Assert.False(diff.HasSemanticChanges);
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Modified);
        Assert.Empty(diff.TypeImpacts);
        Assert.Equal(2, diff.UnchangedCount);
    }

    [Fact]
    public void ControlledEdits_DetectAdditionsDeletionsParametersPlacementGeometryAndRelationships()
    {
        var t1L = MakeWallType(10, "uid-type-1", "Type1", 0.65);
        var t1R = MakeWallType(10, "uid-type-1", "Type1", 0.65);

        var w1L = MakeWall(100, "uid-wall-param", 10, 20.0, 10.0, "before");
        var w1R = MakeWall(100, "uid-wall-param", 10, 20.0, 12.0, "after"); // param & height changed

        var doorL = new Element
        {
            Id = 200, UniqueId = "uid-door-moved", CategoryId = (int)BuiltInCategory.OST_Doors, Category = "OST_Doors",
            HostId = 100, SerializedHostId = 100,
            Transform = new InstanceTransform { Origin = [2, 0, 0], BasisX = [1, 0, 0], BasisY = [0, 1, 0], BasisZ = [0, 0, 1] },
            BoundingBox = [1, -0.5, 0, 4, 0.5, 7],
            Geometry = new GeometryData { BoundingBox = [1, -0.5, 0, 4, 0.5, 7], Faces = [new GeoFace([1, 0, 0], [0, 1, 0]) { Area = 21 }] },
        };
        var doorR = new Element
        {
            Id = 200, UniqueId = "uid-door-moved", CategoryId = (int)BuiltInCategory.OST_Doors, Category = "OST_Doors",
            HostId = 101, SerializedHostId = 101, // relationship change too: moved to another host
            Transform = new InstanceTransform { Origin = [7, 0, 0], BasisX = [1, 0, 0], BasisY = [0, 1, 0], BasisZ = [0, 0, 1] },
            BoundingBox = [6, -0.5, 0, 9, 0.5, 7], // shifted cleanly by +5 ft -> NOT a geometry shape diff!
            Geometry = new GeometryData { BoundingBox = [6, -0.5, 0, 9, 0.5, 7], Faces = [new GeoFace([6, 0, 0], [0, 1, 0]) { Area = 21 }] },
        };

        var wHost2L = MakeWall(101, "uid-wall-2", 10, 15.0);
        var wHost2R = MakeWall(101, "uid-wall-2", 10, 15.0);

        var removedL = new Element { Id = 300, UniqueId = "uid-col-del", CategoryId = (int)BuiltInCategory.OST_Columns, Category = "OST_Columns", Name = "Col" };
        var addedR = new Element { Id = 301, UniqueId = "uid-col-add", CategoryId = (int)BuiltInCategory.OST_Columns, Category = "OST_Columns", Name = "NewCol" };

        var tdLeft = new TransmissionInfo { ExternalReferences = [new ExternalFileRef { ElementId = 500, ReferenceType = "RevitLink", Path = "link_v1.rvt", LoadState = "Loaded" }] };
        var tdRight = new TransmissionInfo { ExternalReferences = [new ExternalFileRef { ElementId = 500, ReferenceType = "RevitLink", Path = "link_v2.rvt", LoadState = "Loaded" }] };

        var left = BuildDoc(ProjectGuidA, [t1L, w1L, wHost2L, doorL, removedL], transmission: tdLeft);
        var right = BuildDoc(ProjectGuidA, [t1R, w1R, wHost2R, doorR, addedR], transmission: tdRight);

        var diff = RvtRevisionDiffer.Compare(left, right);

        Assert.True(diff.HasSemanticChanges);
        Assert.Single(diff.Added, a => a.UniqueId == "uid-col-add");
        Assert.Single(diff.Removed, r => r.UniqueId == "uid-col-del");
        Assert.Single(diff.ExternalLinkChanges);
        Assert.Equal("modified", diff.ExternalLinkChanges[0].Kind);

        var wallMod = diff.Modified.Single(m => m.IdentityKey == "uid-wall-param");
        Assert.Contains(wallMod.ParameterChanges, p => p.Name == "ALL_MODEL_INSTANCE_COMMENTS" && Equals(p.OldValue, "before") && Equals(p.NewValue, "after"));
        Assert.Contains(wallMod.ParameterChanges, p => p.Name == "WALL_USER_HEIGHT_PARAM");
        Assert.NotNull(wallMod.GeometryChange);
        Assert.Equal(RvtDiffAvailability.Confirmed, wallMod.GeometryChange!.Availability);

        var doorMod = diff.Modified.Single(m => m.IdentityKey == "uid-door-moved");
        Assert.NotNull(doorMod.PlacementChange);
        Assert.Equal(5.0, doorMod.PlacementChange!.TranslationDistanceFeet, 4);
        Assert.Null(doorMod.GeometryChange); // pure rigid move does not report shape modification
        Assert.Single(doorMod.RelationshipChanges, rc => rc.Relation == "host" && rc.OldTarget!.UniqueId == "uid-wall-param" && rc.NewTarget!.UniqueId == "uid-wall-2");
    }

    [Fact]
    public void TypeModificationAndReassignment_PreserveLinksToAffectedInstances()
    {
        var typeAL = MakeWallType(10, "uid-type-a", "TypeA", 0.50);
        var typeAR = MakeWallType(10, "uid-type-a", "TypeA", 0.80); // thickness & layers changed
        var typeBL = MakeWallType(11, "uid-type-b", "TypeB", 1.00);
        var typeBR = MakeWallType(11, "uid-type-b", "TypeB", 1.00);

        // Instance 1 keeps TypeA (so TypeA's modification affects it). Instance 2 switches TypeA -> TypeB.
        var inst1L = MakeWall(101, "uid-w1", 10, 10.0);
        var inst1R = MakeWall(101, "uid-w1", 10, 10.0);
        var inst2L = MakeWall(102, "uid-w2", 10, 10.0);
        var inst2R = MakeWall(102, "uid-w2", 11, 10.0);

        var left = BuildDoc(ProjectGuidA, [typeAL, typeBL, inst1L, inst2L]);
        var right = BuildDoc(ProjectGuidA, [typeAR, typeBR, inst1R, inst2R]);

        var diff = RvtRevisionDiffer.Compare(left, right);

        var impactA = diff.TypeImpacts.Single(t => t.TypeElement.IdentityKey == "uid-type-a");
        Assert.True(impactA.LayerChanged);
        Assert.Contains("type-modified", impactA.ChangeKinds);
        Assert.Contains(impactA.AffectedInstances, i => i.IdentityKey == "uid-w1");

        var impactB = diff.TypeImpacts.Single(t => t.TypeElement.IdentityKey == "uid-type-b");
        Assert.Contains("instance-reassigned-to", impactB.ChangeKinds);
        Assert.Contains(impactB.AffectedInstances, i => i.IdentityKey == "uid-w2");

        var modW2 = diff.Modified.Single(m => m.IdentityKey == "uid-w2");
        Assert.NotNull(modW2.TypeChange);
        Assert.Equal("uid-type-a", modW2.TypeChange!.OldTypeUniqueId);
        Assert.Equal("uid-type-b", modW2.TypeChange.NewTypeUniqueId);
    }

    [Fact]
    public void UnrelatedFilesDuplicateUniqueIdsAndPartialDecoding_ReportedExplicitly()
    {
        // 1. Unrelated documents with colliding numeric IDs (missing UniqueId) and distinct GUIDs
        var doc1 = BuildDoc(ProjectGuidA, [
            new Element { Id = 10, CategoryId = (int)BuiltInCategory.OST_Walls, Category = "OST_Walls" },
            new Element { Id = 20, UniqueId = "uid-a", CategoryId = (int)BuiltInCategory.OST_Walls, Category = "OST_Walls" },
        ]);
        var doc2 = BuildDoc(ProjectGuidB, [
            new Element { Id = 10, CategoryId = (int)BuiltInCategory.OST_Walls, Category = "OST_Walls" },
            new Element { Id = 20, UniqueId = "uid-b", CategoryId = (int)BuiltInCategory.OST_Walls, Category = "OST_Walls" },
        ]);

        var unrelated = RvtRevisionDiffer.Compare(doc1, doc2);
        Assert.Equal(RvtRevisionRelation.Unrelated, unrelated.Relation);
        Assert.Empty(unrelated.Modified);
        Assert.Contains(unrelated.Unavailable, u => u.Reason == "missing-persistent-identity");

        // 2. Ambiguous duplicate UniqueId + partial decoding on right (WideLayoutNotRewritten + inexact ElemTable)
        var partialLeft = BuildDoc(ProjectGuidA, [
            new Element { Id = 1, UniqueId = "dup-1", CategoryId = (int)BuiltInCategory.OST_Walls, Category = "OST_Walls" },
            new Element { Id = 2, UniqueId = "dup-1", CategoryId = (int)BuiltInCategory.OST_Walls, Category = "OST_Walls" },
            MakeWall(10, "uid-missing-on-right", 1, 10.0),
            MakeWall(11, "uid-partial-geom", 1, 10.0),
        ], exact: true, streamVer: "14");

        var partialRight = BuildDoc(ProjectGuidA, [
            new Element { Id = 1, UniqueId = "dup-1", CategoryId = (int)BuiltInCategory.OST_Walls, Category = "OST_Walls" },
            new Element { Id = 11, UniqueId = "uid-partial-geom", CategoryId = (int)BuiltInCategory.OST_Walls, Category = "OST_Walls", Geometry = null },
        ], exact: false, wideNotRewritten: true, streamVer: "18");

        var partialDiff = RvtRevisionDiffer.Compare(partialLeft, partialRight);

        Assert.Single(partialDiff.AmbiguousMatches, a => a.IdentityKey == "dup-1" && a.Reason == "duplicate-unique-id");
        // Missing element must NOT be reported as confirmed Removed when right ElemTableIsExact is false!
        Assert.Empty(partialDiff.Removed);
        Assert.Contains(partialDiff.Unavailable, u => u.Aspect == "existence" && u.Element?.UniqueId == "uid-missing-on-right");

        // Matched element whose right geometry is missing under WideLayoutNotRewritten marks geometry Unavailable, not deleted!
        var partialGeomMod = partialDiff.Modified.Single(m => m.IdentityKey == "uid-partial-geom");
        Assert.Contains("geometry", partialGeomMod.UnavailableAspects);
        Assert.Equal(RvtDiffAvailability.Unavailable, partialGeomMod.GeometryChange!.Availability);
    }

    [Fact]
    public void FiltersAndDeterministicJsonAndCliDiff_WorkAsExpected()
    {
        var left = BuildDoc(ProjectGuidA, [
            MakeWallType(10, "uid-t1", "T1", 0.5),
            MakeWall(100, "uid-w1", 10, 10.0, comments: "v1"),
            new Element { Id = 200, UniqueId = "uid-d1", CategoryId = (int)BuiltInCategory.OST_Doors, Category = "OST_Doors", Name = "Door1" },
        ]);
        var right = BuildDoc(ProjectGuidA, [
            MakeWallType(10, "uid-t1", "T1", 0.5),
            MakeWall(100, "uid-w1", 10, 10.0, comments: "v2"),
            new Element { Id = 200, UniqueId = "uid-d1", CategoryId = (int)BuiltInCategory.OST_Doors, Category = "OST_Doors", Name = "Door2" },
            new Element { Id = 201, UniqueId = "uid-d2", CategoryId = (int)BuiltInCategory.OST_Doors, Category = "OST_Doors", Name = "DoorNew" },
        ]);

        var wallsOnly = RvtRevisionDiffer.Compare(left, right, new RvtDiffOptions { Categories = ["OST_Walls"] });
        Assert.Empty(wallsOnly.Added);
        Assert.Single(wallsOnly.Modified);
        Assert.Equal("uid-w1", wallsOnly.Modified[0].IdentityKey);

        string json1 = wallsOnly.ToJson();
        string json2 = wallsOnly.ToJson();
        Assert.Equal(json1, json2);
        using var parsed = JsonDocument.Parse(json1);
        Assert.Equal("rvtio-diff/1", parsed.RootElement.GetProperty("schema").GetString());

        var badOut = new StringWriter();
        var badErr = new StringWriter();
        Assert.Equal(RvtExitCodes.Usage, DiffCommand.Run(["only-one.rvt"], badOut, badErr));
    }

    [SkippableFact]
    public void SampleRvtFilesFromTestRoots_IdenticalModelProducesZeroSemanticChangesAndDeterministicJson()
    {
        string? envRoots = Environment.GetEnvironmentVariable("RVTIO_TEST_FILES");
        if (string.IsNullOrWhiteSpace(envRoots)) return;

        var candidate = envRoots!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.rvt", SearchOption.TopDirectoryOnly))
            .Select(p => new FileInfo(p))
            .OrderBy(fi => fi.Length)
            .FirstOrDefault();
        if (candidate == null) return;

        var diff = RvtRevisionDiffer.Compare(candidate.FullName, candidate.FullName);
        Assert.Equal(RvtRevisionRelation.Related, diff.Relation);
        Assert.False(diff.HasSemanticChanges);
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Modified);

        string j1 = diff.ToJson(indented: false);
        string j2 = diff.ToJson(indented: false);
        Assert.Equal(j1, j2);
    }
}

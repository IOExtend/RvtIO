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

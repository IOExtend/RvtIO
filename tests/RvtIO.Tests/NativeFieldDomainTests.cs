using System.Text.Json;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Scene;
using Xunit;

namespace RvtIO.Tests;

public class NativeFieldDomainTests
{
    private static Parameter P(int id, StorageType storage, object value) => BipHelpers.MakeBipParam(id, id.ToString(), storage, value);
    private static object? Value(Element e, int id) => e.Parameters.FirstOrDefault(p => p.Id == id)?.Value;

    private static Dictionary<uint, ushort> Classes(params (uint Id, ushort Class)[] items) => items.ToDictionary(i => i.Id, i => i.Class);

    private static void Prune(RevitFileInfo info, IReadOnlyDictionary<uint, ushort> classes, bool paged)
    {
        using var stream = new MemoryStream();
        var effects = paged ? new NativeTableEffectsScratch(new PartitionChunks.Backing(stream)) : null;
        if (effects != null)
        {
            foreach (var element in info.Elements)
            {
                effects.Archive(element);
                Assert.Null(element.NativeTableAddedParams);
                Assert.Null(element.NativeTableCreatedParams);
                Assert.Null(element.NativeTablePrevious);
            }
        }
        ElementBuilder.PruneNativeFieldTableOutsideDomains(info, classes, effectsScratch: effects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParameterCreatedByTheTable_IsRemovedOutsideItsDomains_AndKeptInsideThem(bool paged)
    {
        var inside = new Element { Id = 1, CategoryId = -2000011 };
        inside.Parameters.Add(P(-1006000, StorageType.Integer, 5));
        inside.NativeTableAddedParams = [(-1006000, ["10/-2000011"], StorageType.Integer, 5, false)];
        inside.NativeTableCreatedParams = [-1006000];
        var wrongCategory = new Element { Id = 2, CategoryId = -2000023 };
        wrongCategory.Parameters.Add(P(-1006000, StorageType.Integer, 5));
        wrongCategory.NativeTableAddedParams = [(-1006000, ["10/-2000011"], StorageType.Integer, 5, false)];
        wrongCategory.NativeTableCreatedParams = [-1006000];
        var classWide = new Element { Id = 3, CategoryId = -2000023 };
        classWide.Parameters.Add(P(-1006000, StorageType.Integer, 5));
        classWide.NativeTableAddedParams = [(-1006000, ["10"], StorageType.Integer, 5, false)];
        classWide.NativeTableCreatedParams = [-1006000];
        var info = new RevitFileInfo { Elements = [inside, wrongCategory, classWide] };

        Prune(info, Classes((1, 10), (2, 10), (3, 10)), paged);

        Assert.Equal(5, Value(inside, -1006000));
        Assert.Null(Value(wrongCategory, -1006000));
        Assert.Equal(5, Value(classWide, -1006000));
        Assert.Null(inside.NativeTableAddedParams);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverwrittenValue_IsGivenBack_WhenNoRowAppliesToTheFinalDomain(bool paged)
    {
        var element = new Element { Id = 1, CategoryId = -2000023 };
        element.Parameters.Add(P(-1006001, StorageType.String, "wrong"));
        element.NativeTableAddedParams = [(-1006001, ["10/-2000011"], StorageType.String, "wrong", false)];
        string original = new string('x', 40_000) + '\ud800';
        element.NativeTablePrevious = new() { [-1006001] = (original, StorageType.String, false) };

        Prune(new RevitFileInfo { Elements = [element] }, Classes((1, 10)), paged);

        Assert.Equal(original, Value(element, -1006001));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaterPassValue_IsNotTouched(bool paged)
    {
        var element = new Element { Id = 1, CategoryId = -2000023 };
        element.Parameters.Add(P(-1006001, StorageType.String, "decided later"));
        element.NativeTableAddedParams = [(-1006001, ["10/-2000011"], StorageType.String, "table", false)];
        element.NativeTablePrevious = new() { [-1006001] = ("original", StorageType.String, false) };

        Prune(new RevitFileInfo { Elements = [element] }, Classes((1, 10)), paged);

        Assert.Equal("decided later", Value(element, -1006001));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InDomainRow_WinsOverALaterOutOfDomainRow(bool paged)
    {
        var element = new Element { Id = 1, CategoryId = -2000011 };
        element.Parameters.Add(P(-1006002, StorageType.String, ""));
        element.NativeTableAddedParams =
        [
            (-1006002, ["10/-2000011"], StorageType.String, "450x200", false),
            (-1006002, ["10/-2000016"], StorageType.String, "", false),
        ];

        Prune(new RevitFileInfo { Elements = [element] }, Classes((1, 10)), paged);

        Assert.Equal("450x200", Value(element, -1006002));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NegativeElementIdOptionInNativeFieldTable_IsKeptInDomain_AndPrunedOutsideDomain(bool paged)
    {
        var inDomain = new Element { Id = 10, CategoryId = -2000150 };
        var outOfDomain = new Element { Id = 11, CategoryId = -2000575 };
        var npIn = new TfBlockDeserializer.NativeParams();
        npIn.Fields["174:m_centerlineTickMarkStyleId"] = -2;
        var npOut = new TfBlockDeserializer.NativeParams();
        npOut.Fields["174:m_centerlineTickMarkStyleId"] = -2;

        using var partitionStream = new MemoryStream();
        using var sceneStream = new MemoryStream();
        var scratch = new SceneMeshScratch(sceneStream);
        var state = new ParsingState
        {
            KeepExportProvenance = !paged,
            Chunks = paged ? new PartitionChunks(new PartitionChunks.Backing(partitionStream)) : [],
            ArchiveSceneParameters = paged ? scratch.ArchiveParameters : null,
        };
        state.ElemClassIdx[10] = 174;
        state.ElemClassIdx[11] = 174;
        state.NativeParams[10] = npIn;
        state.NativeParams[11] = npOut;

        var info = new RevitFileInfo { Elements = [inDomain, outOfDomain] };
        ElementBuilder.PostProcess(info, state);
        if (paged) Assert.True(partitionStream.Length > 0);

        var keptParam = Assert.Single(inDomain.Parameters, p => p.Id == -1006445);
        Assert.Equal(-2, keptParam.Value);
        Assert.True(keptParam.HasRevitValue);

        Assert.DoesNotContain(outOfDomain.Parameters, p => p.Id == -1006445);
        string outJson = JsonSerializer.Serialize(new RevitFileInfo { Elements = [outOfDomain] });
        Assert.DoesNotContain("-1006445", outJson);
    }

    [Fact]
    public void UnsetClassifiedNonMinusOneFieldInNativeFieldTable_StillChecksDomains()
    {
        var inDomainTickZero = new Element { Id = 20, CategoryId = -2000150 };
        var outOfDomainTickZero = new Element { Id = 21, CategoryId = -2000575 };
        var inDomainHostMinusFour = new Element { Id = 22, CategoryId = -2000014 };
        var outOfDomainHostMinusFour = new Element { Id = 23, CategoryId = -2001330 };

        var np20 = new TfBlockDeserializer.NativeParams();
        np20.Fields["174:m_centerlineTickMarkStyleId"] = 0;
        var np21 = new TfBlockDeserializer.NativeParams();
        np21.Fields["174:m_centerlineTickMarkStyleId"] = 0;
        var np22 = new TfBlockDeserializer.NativeParams();
        np22.Fields["1795:m_hostId"] = -4;
        var np23 = new TfBlockDeserializer.NativeParams();
        np23.Fields["1795:m_hostId"] = -4;

        var state = new ParsingState();
        state.ElemClassIdx[20] = 174;
        state.ElemClassIdx[21] = 174;
        state.ElemClassIdx[22] = 1794;
        state.ElemClassIdx[23] = 1794;
        state.NativeParams[20] = np20;
        state.NativeParams[21] = np21;
        state.NativeParams[22] = np22;
        state.NativeParams[23] = np23;

        var info = new RevitFileInfo
        {
            Elements = [inDomainTickZero, outOfDomainTickZero, inDomainHostMinusFour, outOfDomainHostMinusFour],
        };
        ElementBuilder.PostProcess(info, state);

        var tickInDomain = Assert.Single(inDomainTickZero.Parameters, p => p.Id == -1006445);
        Assert.Equal(0, tickInDomain.Value);
        Assert.True(tickInDomain.HasValue);
        Assert.False(tickInDomain.HasRevitValue);

        Assert.DoesNotContain(outOfDomainTickZero.Parameters, p => p.Id == -1006445);
        string outTickJson = JsonSerializer.Serialize(new RevitFileInfo { Elements = [outOfDomainTickZero] });
        Assert.DoesNotContain("-1006445", outTickJson);

        var hostInDomain = Assert.Single(inDomainHostMinusFour.Parameters, p => p.Id == -1002108);
        Assert.Equal(-4, hostInDomain.Value);
        Assert.True(hostInDomain.HasValue);
        Assert.False(hostInDomain.HasRevitValue);

        Assert.NotEqual(-4, Value(outOfDomainHostMinusFour, -1002108));
        var hostOutParam = outOfDomainHostMinusFour.Parameters.FirstOrDefault(p => p.Id == -1002108);
        if (hostOutParam != null)
            Assert.False(hostOutParam.HasRevitValue);
    }

    [Fact]
    public void ZeroProjectPhaseInNativeFields_HasRevitValueTrue_WhileUnsetZeroInSameModelStaysFalse()
    {
        var phaseZero = new Element
        {
            Id = 0,
            CategoryId = -2000112,
            Category = "OST_Phases",
            Name = "Phase 0",
        };
        var beam = new Element
        {
            Id = 10,
            CategoryId = -2001320,
            Category = "OST_StructuralFraming",
            Name = "Beam",
            References = [0],
            Parameters =
            [
                new Parameter { Id = 999002, StorageType = StorageType.ElementId, Value = 7 },
            ],
        };
        var info = new RevitFileInfo
        {
            Elements = [phaseZero, beam],
        };
        var np = new TfBlockDeserializer.NativeParams();
        np.Fields["m_createdPhaseId"] = 0;
        np.Fields["m_demolishedPhaseId"] = 0;
        np.ElementIds.Add((-1012100, 0)); // PHASE_CREATED = ProjectPhase 0
        np.ElementIds.Add((-1012101, 0)); // PHASE_DEMOLISHED = ProjectPhase 0
        np.ElementIds.Add((-1002050, 0)); // ELEM_TYPE_PARAM = 0 (unset; target 0 is a phase, not a type)
        np.ElementIds.Add((-1002108, 0)); // HOST_ID_PARAM = 0 (unhosted)
        np.ElementIds.Add((999001, 0));    // arbitrary unset ElementId = 0
        np.NamedParams.Add(new TfBlockDeserializer.NamedParamRecord(999002, null, 0, 0, 0, true, false)); // unset named ElementId = 0

        var state = new ParsingState();
        state.ElemClassIdx[0] = 3065;
        state.NativeParams[10] = np;
        state.NativeParamsFromRootClass.Add(10);

        ElementBuilder.PostProcess(info, state);

        var phaseCreated = beam.Parameters.Single(p => p.Id == -1012100);
        var phaseDemolished = beam.Parameters.Single(p => p.Id == -1012101);
        var unsetType = beam.Parameters.Single(p => p.Id == -1002050);
        var unsetHost = beam.Parameters.Single(p => p.Id == -1002108);
        var unsetZero = beam.Parameters.Single(p => p.Id == 999001);
        var unsetNamedZero = beam.Parameters.Single(p => p.Id == 999002);

        Assert.True(phaseCreated.HasRevitValue);
        Assert.Equal(0, phaseCreated.Value);
        Assert.True(phaseDemolished.HasRevitValue);
        Assert.Equal(0, phaseDemolished.Value);
        Assert.False(unsetType.HasRevitValue);
        Assert.False(unsetHost.HasRevitValue);
        Assert.False(unsetZero.HasRevitValue);
        Assert.False(unsetNamedZero.HasRevitValue);

        // Older 32-bit phase-0 files store phase 0 in ProjectPhaseOrderSource without an Element { Id = 0 } row.
        var legacyBeam = new Element
        {
            Id = 10,
            CategoryId = -2001320,
            Category = "OST_StructuralFraming",
            Name = "Beam",
            References = [0],
        };
        var legacyInfo = new RevitFileInfo { Elements = [legacyBeam] };
        var legacyNp = new TfBlockDeserializer.NativeParams();
        legacyNp.Fields["m_createdPhaseId"] = 0;
        legacyNp.ElementIds.Add((-1012100, 0));
        legacyNp.ElementIds.Add((999001, 0));
        var legacyState = new ParsingState
        {
            ProjectPhaseOrderSource = (1, [0, 2]),
        };
        legacyState.NativeParams[10] = legacyNp;
        legacyState.NativeParamsFromRootClass.Add(10);

        ElementBuilder.PostProcess(legacyInfo, legacyState);

        var legacyPhase = legacyBeam.Parameters.Single(p => p.Id == -1012100);
        var legacyUnsetZero = legacyBeam.Parameters.Single(p => p.Id == 999001);
        Assert.True(legacyPhase.HasRevitValue);
        Assert.False(legacyUnsetZero.HasRevitValue);

        var legacyDoc = RvtDocument.FromInfo(legacyInfo);
        Assert.True(legacyDoc.GetElement(10)!.Parameters.Single(p => p.Id == -1012100).HasRevitValue);
        Assert.False(legacyDoc.GetElement(10)!.Parameters.Single(p => p.Id == 999001).HasRevitValue);
    }
}

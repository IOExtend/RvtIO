using System.Text.Json;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class NativeFieldDomainTests
{
    private static Parameter P(int id, StorageType storage, object value) => BipHelpers.MakeBipParam(id, id.ToString(), storage, value);
    private static object? Value(Element e, int id) => e.Parameters.FirstOrDefault(p => p.Id == id)?.Value;

    private static Dictionary<uint, ushort> Classes(params (uint Id, ushort Class)[] items) => items.ToDictionary(i => i.Id, i => i.Class);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ListFactsKeepRuleOrderAndOnlyFamilyInstancesKeepElementBaseFacts(bool familyInstance)
    {
        var state = new ParsingState();
        var native = state.NativeParams[1] = new TfBlockDeserializer.NativeParams();
        native.Fields["527:m_leader"] = 4;
        if (familyInstance) native.Fields["1794:m_masterSymbolId"] = 3;
        native.Fields["37:m_dummy"] = 2;
        native.Fields["160:m_flags"] = 1;
        var element = new Element { Id = 1 };

        var info = new RevitFileInfo { Elements = [element] };
        ElementBuilder.ApplyNativeParams(info, state);
        ElementBuilder.ApplyMepSystemParams(info, state, null);

        string[] expected = familyInstance
            ? ["160:m_flags", "37:m_dummy", "1794:m_masterSymbolId", "527:m_leader"]
            : ["160:m_flags", "527:m_leader"];
        Assert.Equal(expected, element.NativeListFields!.Keys);
        Assert.Equal(expected.Select(key => native.Fields[key]), element.NativeListFields.Values);
        Assert.Null(native.CapturedDoubleFields);
        Assert.Null(native.CapturedStringFields);
    }

    [Fact]
    public void NativeHostsKeepSeparateNumericallyOrderedListsWithSparseIndices()
    {
        var state = new ParsingState();
        var native = state.NativeParams[1] = new TfBlockDeserializer.NativeParams();
        native.Fields["m_extraHostIds@10"] = 20;
        native.Fields["m_explicitHostIds@3"] = -1;
        native.Fields["m_extraHostIds@2"] = 17;
        native.Fields["m_explicitHostIds@0"] = 14;
        native.Fields["m_extraHostIdsOther"] = 99;
        var element = new Element { Id = 1 };
        var info = new RevitFileInfo { Elements = [element] };
        ElementBuilder.ApplyNativeParams(info, state);
        Assert.Equal([17, 20], element.ExtraHostIds);
        Assert.Equal([14, -1], element.ExplicitHostIds);
        native.Fields.Clear();
        ElementBuilder.ApplyNativeParams(info, state);
        Assert.Null(element.ExtraHostIds);
        Assert.Null(element.ExplicitHostIds);
    }

    [Fact]
    public void NativeParameterIndexKeepsFirstMatchAndEachElementsStoreIds()
    {
        const int height = -1001300, namedOnly = -1001206;
        var first = new Parameter { Id = height };
        var duplicate = P(height, StorageType.Double, 9.0);
        var one = new Element { Id = 10, Parameters = [first, duplicate] };
        var two = new Element { Id = 11 };
        var state = new ParsingState();
        var nativeOne = state.NativeParams[10] = new TfBlockDeserializer.NativeParams();
        nativeOne.Doubles.Add((height, 4.5));
        nativeOne.NamedParams.Add(new(namedOnly, "2 h", 0, 0, 0, false, false));
        var nativeTwo = state.NativeParams[11] = new TfBlockDeserializer.NativeParams();
        nativeTwo.Doubles.Add((height, 8.0));

        ElementBuilder.ApplyNativeParams(new RevitFileInfo { Elements = [one, two] }, state);

        Assert.Equal(4.5, first.Value);
        Assert.Equal(9.0, duplicate.Value);
        Assert.Equal(8.0, Value(two, height));
        Assert.Equal([height], one.NativeParameterStoreIds);
        Assert.Equal([height, namedOnly], one.NativeStoredParameterIds);
        Assert.Equal([height], two.NativeStoredParameterIds);
    }

    [Fact]
    public void ParameterCreatedByTheTable_IsRemovedOutsideItsDomains_AndKeptInsideThem()
    {
        var inside = new Element { Id = 1, CategoryId = -2000011 };
        inside.Parameters.Add(P(-1006000, StorageType.Integer, 5));
        inside.NativeTableAddedParams = [new(-1006000, ["10/-2000011"], StorageType.Integer, 5, false, new(Created: true))];
        var wrongCategory = new Element { Id = 2, CategoryId = -2000023 };
        wrongCategory.Parameters.Add(P(-1006000, StorageType.Integer, 5));
        wrongCategory.NativeTableAddedParams = [new(-1006000, ["10/-2000011"], StorageType.Integer, 5, false, new(Created: true))];
        var classWide = new Element { Id = 3, CategoryId = -2000023 };
        classWide.Parameters.Add(P(-1006000, StorageType.Integer, 5));
        classWide.NativeTableAddedParams = [new(-1006000, ["10"], StorageType.Integer, 5, false, new(Created: true))];
        var info = new RevitFileInfo { Elements = [inside, wrongCategory, classWide] };

        ElementBuilder.PruneNativeFieldTableOutsideDomains(info, Classes((1, 10), (2, 10), (3, 10)));

        Assert.Equal(5, Value(inside, -1006000));
        Assert.Null(Value(wrongCategory, -1006000));
        Assert.Equal(5, Value(classWide, -1006000));
        Assert.Null(inside.NativeTableAddedParams);
    }

    [Fact]
    public void OverwrittenValue_IsGivenBack_WhenNoRowAppliesToTheFinalDomain()
    {
        var element = new Element { Id = 1, CategoryId = -2000023 };
        element.Parameters.Add(P(-1006001, StorageType.String, "wrong"));
        element.NativeTableAddedParams = [new(-1006001, ["10/-2000011"], StorageType.String, "wrong", false,
            new(false, StorageType.String, "original"))];

        ElementBuilder.PruneNativeFieldTableOutsideDomains(new RevitFileInfo { Elements = [element] }, Classes((1, 10)));

        Assert.Equal("original", Value(element, -1006001));
    }

    [Fact]
    public void LaterPassValue_IsNotTouched()
    {
        var element = new Element { Id = 1, CategoryId = -2000023 };
        element.Parameters.Add(P(-1006001, StorageType.String, "decided later"));
        element.NativeTableAddedParams = [new(-1006001, ["10/-2000011"], StorageType.String, "table", false,
            new(false, StorageType.String, "original"))];

        ElementBuilder.PruneNativeFieldTableOutsideDomains(new RevitFileInfo { Elements = [element] }, Classes((1, 10)));

        Assert.Equal("decided later", Value(element, -1006001));
    }

    [Fact]
    public void InDomainRow_WinsOverALaterOutOfDomainRow()
    {
        var element = new Element { Id = 1, CategoryId = -2000011 };
        element.Parameters.Add(P(-1006002, StorageType.String, ""));
        element.NativeTableAddedParams =
        [
            new(-1006002, ["10/-2000011"], StorageType.String, "450x200", false),
            new(-1006002, ["10/-2000016"], StorageType.String, "", false),
        ];

        ElementBuilder.PruneNativeFieldTableOutsideDomains(new RevitFileInfo { Elements = [element] }, Classes((1, 10)));

        Assert.Equal("450x200", Value(element, -1006002));
    }

    [Fact]
    public void RepeatedNativePassKeepsFirstSnapshotUntilAnExplicitStoreTakesOver()
    {
        const int tick = -1006445;
        const int outsideTickDomain = -2000575;
        var created = new Element { Id = 10, CategoryId = outsideTickDomain };
        var original = new Parameter { Id = tick, StorageType = StorageType.None, HasNoRevitValue = true };
        var overwritten = new Element { Id = 11, CategoryId = outsideTickDomain, Parameters = [original] };
        var stored = new Element { Id = 12, CategoryId = outsideTickDomain };
        var info = new RevitFileInfo { Elements = [created, overwritten, stored] };
        var state = new ParsingState();
        foreach (var element in info.Elements)
        {
            state.ElemClassIdx[(uint)element.Id] = 174;
            var native = state.NativeParams[(uint)element.Id] = new TfBlockDeserializer.NativeParams();
            native.Fields["174:m_centerlineTickMarkStyleId"] = 25;
        }

        ElementBuilder.ApplyNativeParams(info, state);
        Assert.Single(stored.Parameters, p => p.Id == tick).Value = null;
        state.NativeParams[12].ElementIds.Add((tick, 42));
        ElementBuilder.ApplyNativeParams(info, state);
        Assert.All(info.Elements, e => Assert.Equal(25, Value(e, tick)));
        ElementBuilder.PruneNativeFieldTableOutsideDomains(info, state.ElemClassIdx);

        Assert.DoesNotContain(created.Parameters, p => p.Id == tick);
        Assert.Same(original, Assert.Single(overwritten.Parameters, p => p.Id == tick));
        Assert.Null(original.Value);
        Assert.Equal(StorageType.None, original.StorageType);
        Assert.True(original.HasNoRevitValue);
        Assert.Equal(42, Value(stored, tick));
    }

    [Fact]
    public void NegativeElementIdOptionInNativeFieldTable_IsKeptInDomain_AndPrunedOutsideDomain()
    {
        var inDomain = new Element { Id = 10, CategoryId = -2000150 };
        var outOfDomain = new Element { Id = 11, CategoryId = -2000575 };
        var npIn = new TfBlockDeserializer.NativeParams();
        npIn.Fields["174:m_centerlineTickMarkStyleId"] = -2;
        var npOut = new TfBlockDeserializer.NativeParams();
        npOut.Fields["174:m_centerlineTickMarkStyleId"] = -2;

        var state = new ParsingState();
        state.ElemClassIdx[10] = 174;
        state.ElemClassIdx[11] = 174;
        state.NativeParams[10] = npIn;
        state.NativeParams[11] = npOut;

        var info = new RevitFileInfo { Elements = [inDomain, outOfDomain] };
        ElementBuilder.PostProcess(info, state);

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

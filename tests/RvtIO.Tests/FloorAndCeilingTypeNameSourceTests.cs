using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class RvtFileReaderSlabAndCeilingTypeNameSourceTests
{
    [Fact]
    public void RecordTypeNameSource_RoutesOnlyExactTypeSideClassesAndInvalidatesDuplicates()
    {
        var state = new ParsingState();

        RvtFileReader.RecordTypeNameSource(
            state, 0xFFFF077B, 101, "Canonical floor type");
        RvtFileReader.RecordTypeNameSource(
            state, 0xFFFF0286, 201, "Canonical ceiling type");
        RvtFileReader.RecordTypeNameSource(
            state, 0xFFFF0779, 102, "Floor instance-side name");
        RvtFileReader.RecordTypeNameSource(
            state, 0xFFFF0282, 202, "Ceiling instance-side name");

        Assert.Equal(
            (1, "Canonical floor type"), state.FloorTypeNameSource[101]);
        Assert.Equal(
            (1, "Canonical ceiling type"), state.CeilingTypeNameSource[201]);
        Assert.DoesNotContain((uint)102, state.FloorTypeNameSource.Keys);
        Assert.DoesNotContain((uint)202, state.CeilingTypeNameSource.Keys);

        RvtFileReader.RecordTypeNameSource(
            state, 0xFFFF077B, 101, "Conflicting floor type");

        Assert.Equal(2, state.FloorTypeNameSource[101].BlockCount);
        Assert.Null(state.FloorTypeNameSource[101].TerminalName);
        Assert.Single(state.FloorTypeNameSource);
        Assert.Single(state.CeilingTypeNameSource);
    }
}

public class ElementBuilderFloorTypeNameSourceTests
{
    private const int FloorsCategoryId = -2000032;

    [Fact]
    public void PostProcess_StoresUniqueOwnBlockNameOnlyAsIsolatedReferenceSource()
    {
        var type = Floor(200, 0, "Existing floor type name");
        var instance = Floor(300, type.Id, "Existing floor instance name");
        var info = new RevitFileInfo();
        info.Elements.AddRange([type, instance]);
        var state = new ParsingState();
        state.NativeParams[(uint)instance.Id] = FloorNativeParams(type.Id);
        state.FloorTypeNameSource[(uint)type.Id] =
            (1, "Canonical opening Reference");

        ElementBuilder.PostProcess(info, state);

        Assert.Equal("Existing floor type name", type.Name);
        // The ordinary type-name propagation predates this rule. The 0xFFFF077B value
        // must not replace that baseline main-product name.
        Assert.Equal("Existing floor type name", instance.Name);
        Assert.NotEqual("Canonical opening Reference", instance.Name);
        Assert.Null(type.ValidatedFloorTypeReferenceName);
        Assert.Equal(
            "Canonical opening Reference",
            instance.ValidatedFloorTypeReferenceName);
    }

    [Theory]
    [InlineData("wrong-instance-category")]
    [InlineData("wrong-type-category")]
    [InlineData("missing-native-row")]
    [InlineData("missing-native-field")]
    [InlineData("nonpositive-type")]
    [InlineData("self-type")]
    [InlineData("unresolved-type")]
    [InlineData("missing-source")]
    [InlineData("multiple-blocks")]
    [InlineData("missing-terminal-name")]
    [InlineData("roof-field-only")]
    public void FloorOpeningReferenceName_FailsClosedWithoutCompleteValidation(
        string brokenValidation)
    {
        var type = Floor(200, 0, "Existing floor type name");
        var instance = Floor(300, type.Id, "Existing floor instance name");
        instance.ValidatedFloorTypeReferenceName = "Stale value";
        var elements = new List<Element> { type, instance };
        var source = new Dictionary<uint,
            (int BlockCount, string? TerminalName)>
        {
            [(uint)type.Id] = (1, "Unvalidated replacement"),
        };
        var nativeParams = new Dictionary<uint,
            TfBlockDeserializer.NativeParams>
        {
            [(uint)instance.Id] = FloorNativeParams(type.Id),
        };

        switch (brokenValidation)
        {
            case "wrong-instance-category":
                instance.CategoryId = -2000038;
                break;
            case "wrong-type-category":
                type.CategoryId = -2000038;
                break;
            case "missing-native-row":
                nativeParams.Clear();
                break;
            case "missing-native-field":
                nativeParams[(uint)instance.Id] = new();
                break;
            case "nonpositive-type":
                nativeParams[(uint)instance.Id] = FloorNativeParams(0);
                break;
            case "self-type":
                nativeParams[(uint)instance.Id] =
                    FloorNativeParams(instance.Id);
                source[(uint)instance.Id] = (1, "Unvalidated replacement");
                break;
            case "unresolved-type":
                nativeParams[(uint)instance.Id] = FloorNativeParams(999);
                break;
            case "missing-source":
                source.Clear();
                break;
            case "multiple-blocks":
                source[(uint)type.Id] = (2, null);
                break;
            case "missing-terminal-name":
                source[(uint)type.Id] = (1, null);
                break;
            case "roof-field-only":
                nativeParams[(uint)instance.Id] =
                    FloorNativeParams(type.Id, "m_roofAttributesId");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(brokenValidation));
        }

        ElementBuilder.ApplyValidatedFloorTypeReferenceNames(
            elements, elements.ToDictionary(element => element.Id), source,
            nativeParams);

        Assert.Equal("Existing floor type name", type.Name);
        Assert.Equal("Existing floor instance name", instance.Name);
        Assert.Null(instance.ValidatedFloorTypeReferenceName);
    }

    [Fact]
    public void FloorReferenceValidation_UsesRawFloorAttributeInsteadOfPreliminaryTypeId()
    {
        var type = Floor(200, 0, "Existing floor type name");
        var wrongPreliminaryType = Floor(250, 0, "Wrong preliminary type");
        var instance = Floor(300, wrongPreliminaryType.Id, "Existing instance name");
        var elements = new List<Element> { type, wrongPreliminaryType, instance };
        var source = new Dictionary<uint,
            (int BlockCount, string? TerminalName)>
        {
            [(uint)type.Id] = (1, "Canonical floor Reference"),
        };
        var nativeParams = new Dictionary<uint,
            TfBlockDeserializer.NativeParams>
        {
            [(uint)instance.Id] = FloorNativeParams(type.Id),
        };

        ElementBuilder.ApplyValidatedFloorTypeReferenceNames(
            elements, elements.ToDictionary(element => element.Id), source,
            nativeParams);

        Assert.Equal(wrongPreliminaryType.Id, instance.TypeId);
        Assert.Equal("Canonical floor Reference",
            instance.ValidatedFloorTypeReferenceName);
        Assert.Equal("Existing instance name", instance.Name);
    }

    [Fact]
    public void PostProcess_AppliesFloorValidationAfterNativeTypeAndCategoryPropagation()
    {
        var type = Floor(200, 0, "Existing floor type name");
        var instance = Floor(300, 999, "Existing instance name");
        instance.CategoryId = null;
        instance.Category = null;
        var info = new RevitFileInfo();
        info.Elements.AddRange([type, instance]);
        var state = new ParsingState();
        state.NativeParams[(uint)instance.Id] = FloorNativeParams(type.Id);
        state.FloorTypeNameSource[(uint)type.Id] =
            (1, "Canonical floor Reference");

        ElementBuilder.PostProcess(info, state);

        Assert.Equal(FloorsCategoryId, instance.CategoryId);
        Assert.Equal(type.Id, instance.TypeId);
        Assert.Equal("Canonical floor Reference",
            instance.ValidatedFloorTypeReferenceName);
        Assert.Equal("Existing instance name", instance.Name);
    }

    [Fact]
    public void FloorReferenceValidation_UsesExplicitInstanceThenExactRawTypeBeforeTerminalName()
    {
        var type = Floor(200, 0, "Existing floor type name");
        type.Parameters.Add(StringParameter("Reference", "Type Reference", 20));
        var instance = Floor(300, 999, "Existing instance name");
        instance.Parameters.Add(StringParameter("Reference", "Instance Reference", 10));
        var elements = new List<Element> { type, instance };

        ElementBuilder.ApplyValidatedFloorTypeReferenceNames(
            elements, elements.ToDictionary(element => element.Id),
            new Dictionary<uint, (int BlockCount, string? TerminalName)>(),
            new Dictionary<uint, TfBlockDeserializer.NativeParams>
            {
                [(uint)instance.Id] = FloorNativeParams(type.Id),
            });

        Assert.Equal("Instance Reference", instance.ValidatedFloorTypeReferenceName);
        Assert.True(instance.ValidatedFloorTypeReferenceIsExplicit);
        Assert.False(instance.ValidatedFloorTypeReferenceIsBlocked);
        Assert.Equal("Existing instance name", instance.Name);
    }

    [Fact]
    public void FloorReferenceValidation_EmptyInstanceFallsThroughToExactRawTypeSuffix()
    {
        var type = Floor(200, 0, "Existing floor type name");
        type.Parameters.Add(StringParameter("Reference[Type]", "Type suffix", 20));
        var instance = Floor(300, 999, "Existing instance name");
        instance.Parameters.Add(StringParameter("Reference", "", 10));
        var elements = new List<Element> { type, instance };

        ElementBuilder.ApplyValidatedFloorTypeReferenceNames(
            elements, elements.ToDictionary(element => element.Id),
            new Dictionary<uint, (int BlockCount, string? TerminalName)>
            {
                [(uint)type.Id] = (1, "Terminal type name"),
            },
            new Dictionary<uint, TfBlockDeserializer.NativeParams>
            {
                [(uint)instance.Id] = FloorNativeParams(type.Id),
            });

        Assert.Equal("Type suffix", instance.ValidatedFloorTypeReferenceName);
        Assert.True(instance.ValidatedFloorTypeReferenceIsExplicit);
        Assert.False(instance.ValidatedFloorTypeReferenceIsBlocked);
    }

    [Theory]
    [InlineData("agreeing", "Own type name")]
    [InlineData("two-blocks", null)]
    [InlineData("other-class", null)]
    [InlineData("no-layers", null)]
    [InlineData("other-discriminator", null)]
    [InlineData("block-only", null)]
    public void FloorReferenceValidation_OwnTypeNameNeedsTheValidationOfTheHostTypes(string mode, string? expectedOwnName)
    {
        var type = Floor(200, 0, "Existing floor type name");
        var instance = Floor(300, type.Id, "Existing instance name");
        type.IsBlockOnly = mode == "block-only";
        var elements = new List<Element> { type, instance };
        var state = new ParsingState();
        state.SystemTypeRoots[(uint)type.Id] = (mode == "two-blocks" ? 2 : 1, 1915, mode == "other-discriminator" ? 1 : 0);
        state.HostTypeCompoundStructures[(uint)type.Id] = (mode == "two-blocks" ? 2 : 1, (ushort)(mode == "other-class" ? 646 : 1915),
            mode == "no-layers" ? null : [new RawCompoundStructureLayer(0, 0, 0, 1, 0, 0, 0)], "Own type name");

        ElementBuilder.ApplyValidatedFloorTypeReferenceNames(
            elements, elements.ToDictionary(element => element.Id),
            new Dictionary<uint, (int BlockCount, string? TerminalName)>
            {
                // the block of a type with text parameters ends in the last of them
                [(uint)type.Id] = (1, "Terminal string"),
            },
            new Dictionary<uint, TfBlockDeserializer.NativeParams>
            {
                [(uint)instance.Id] = FloorNativeParams(type.Id),
            },
            state);

        Assert.Equal(expectedOwnName, instance.ValidatedFloorOwnTypeName);
        Assert.Equal("Terminal string", instance.ValidatedFloorTypeReferenceName);
        Assert.False(instance.ValidatedFloorTypeReferenceIsExplicit);
    }

    [Theory]
    [InlineData(-2000032, -2001300, 1)] // instance: Floors (-2000032), type: StructuralFoundation (-2001300)
    [InlineData(-2001300, -2001300, 1)] // instance: StructuralFoundation (-2001300), type: StructuralFoundation (-2001300)
    public void FloorReferenceValidation_FoundationSlab_AppliesValidatedTypeAndReferenceNames(
        int instanceCategory, int typeCategory, int discriminator)
    {
        var type = Floor(200, 0, "Foundation slab type");
        type.CategoryId = typeCategory;
        var instance = Floor(300, type.Id, "Foundation slab instance");
        instance.CategoryId = instanceCategory;
        var elements = new List<Element> { type, instance };
        var state = new ParsingState();
        state.SystemTypeRoots[(uint)type.Id] = (1, 1915, discriminator);
        state.HostTypeCompoundStructures[(uint)type.Id] = (1, (ushort)1915,
            [new RawCompoundStructureLayer(0, 0, 0, 1, 0, 0, 0)], "Authored foundation slab type");

        ElementBuilder.ApplyValidatedFloorTypeReferenceNames(
            elements, elements.ToDictionary(element => element.Id),
            new Dictionary<uint, (int BlockCount, string? TerminalName)>
            {
                [(uint)type.Id] = (1, "Terminal foundation string"),
            },
            new Dictionary<uint, TfBlockDeserializer.NativeParams>
            {
                [(uint)instance.Id] = FloorNativeParams(type.Id),
            },
            state);

        Assert.Equal("Authored foundation slab type", instance.ValidatedFloorOwnTypeName);
        Assert.Equal("Terminal foundation string", instance.ValidatedFloorTypeReferenceName);
        Assert.False(instance.ValidatedFloorTypeReferenceIsExplicit);

        Assert.Equal("Authored foundation slab type", IfcExporter.FloorTypeReferenceName(instance, currentProfile: true));
        Assert.Equal("Terminal foundation string", IfcExporter.FloorTypeReferenceName(instance, currentProfile: false));

        string expectedQtoCurrent = instanceCategory == -2000032 ? "Authored foundation slab type" : "Competing string";
        string expectedQtoNonCurrent = instanceCategory == -2000032 ? "Terminal foundation string" : "Competing string";
        Assert.Equal(expectedQtoCurrent,
            IfcExporter.ResolveQuantityTakeOffReferenceValue(instance, "IFCSLAB", "Competing string", currentProfile: true));
        Assert.Equal(expectedQtoNonCurrent,
            IfcExporter.ResolveQuantityTakeOffReferenceValue(instance, "IFCSLAB", "Competing string", currentProfile: false));
    }

    [Fact]
    public void FloorReferenceValidation_OwnTypeNameIsForTheCurrentProfileOnly()
    {
        var floor = Floor(300, 0, "Floor");
        floor.ValidatedFloorTypeReferenceName = "Terminal string";
        floor.ValidatedFloorOwnTypeName = "Own type name";
        Assert.Equal("Own type name", IfcExporter.FloorTypeReferenceName(floor, currentProfile: true));
        Assert.Equal("Terminal string", IfcExporter.FloorTypeReferenceName(floor, currentProfile: false));
        floor.ValidatedFloorOwnTypeName = null;
        Assert.Equal("Terminal string", IfcExporter.FloorTypeReferenceName(floor, currentProfile: true));
    }

    [Fact]
    public void FloorReferenceValidation_UnreproducibleExplicitParameterBlocksTerminalFallback()
    {
        var type = Floor(200, 0, "Existing floor type name");
        var instance = Floor(300, type.Id, "Existing instance name");
        instance.Parameters.Add(new Parameter
        {
            Id = 10,
            Definition = new Definition { Id = 10, Name = "Reference" },
            StorageType = StorageType.Double,
            Value = 12.5,
        });
        var elements = new List<Element> { type, instance };

        ElementBuilder.ApplyValidatedFloorTypeReferenceNames(
            elements, elements.ToDictionary(element => element.Id),
            new Dictionary<uint, (int BlockCount, string? TerminalName)>
            {
                [(uint)type.Id] = (1, "Must not leak"),
            },
            new Dictionary<uint, TfBlockDeserializer.NativeParams>
            {
                [(uint)instance.Id] = FloorNativeParams(type.Id),
            });

        Assert.Null(instance.ValidatedFloorTypeReferenceName);
        Assert.False(instance.ValidatedFloorTypeReferenceIsExplicit);
        Assert.True(instance.ValidatedFloorTypeReferenceIsBlocked);
        Assert.Equal("Existing instance name", instance.Name);
    }

    private static TfBlockDeserializer.NativeParams FloorNativeParams(
        int typeId, string fieldName = "m_floorAttributesId")
    {
        var nativeParams = new TfBlockDeserializer.NativeParams();
        nativeParams.Fields[fieldName] = typeId;
        return nativeParams;
    }

    private static Element Floor(int id, int typeId, string name) => new()
    {
        Id = id,
        TypeId = typeId,
        CategoryId = FloorsCategoryId,
        Category = "OST_Floors",
        Name = name,
    };

    private static Parameter StringParameter(
        string name, string value, int id) => new()
    {
        Id = id,
        Definition = new Definition { Id = id, Name = name },
        StorageType = StorageType.String,
        Value = value,
    };
}

public class ElementBuilderCeilingTypeNameSourceTests
{
    private const int CeilingsCategoryId = -2000038;
    private const ushort CeilingClassId = 642;

    [Fact]
    public void ExtractParams_CeilingOwnBlockCapturesSerializedAttributeId()
    {
        const int occurrenceId = 300;
        const int ceilingTypeId = 200;
        var (block, dataSize) = CreateCeilingBlock(
            occurrenceId, ceilingTypeId);

        TfBlockDeserializer.NativeParams? decoded =
            TfBlockDeserializer.ExtractParams(
                block, 0, dataSize, CeilingClassId);

        Assert.NotNull(decoded);
        Assert.Equal(occurrenceId, decoded.Fields["m_id"]);
        Assert.Equal(
            ceilingTypeId, decoded.Fields["m_ceilingAttributesId"]);
    }

    [Fact]
    public void ApplyValidatedCeilingTypeNames_PropagatesUniqueOwnBlockNameThroughResolvedType()
    {
        var type = Ceiling(200, 0, "Wrong parsed type");
        var instance = Ceiling(300, type.Id, "Wrong instance name");
        var elements = new List<Element> { type, instance };
        var source = new Dictionary<uint,
            (int BlockCount, string? TerminalName)>
        {
            [(uint)type.Id] = (1, "Canonical ceiling type"),
        };

        ElementBuilder.ApplyValidatedCeilingTypeNames(
            elements, elements.ToDictionary(element => element.Id), source);

        Assert.Equal("Canonical ceiling type", type.Name);
        Assert.Equal("Canonical ceiling type", instance.Name);
    }

    [Fact]
    public void PostProcess_UsesOwnSerializedCeilingLinkWithoutChangingStructuralType()
    {
        var serializedType = Ceiling(200, 0, "Wrong serialized type name");
        var preliminaryType = Ceiling(250, 0, "Wrong preliminary type");
        var instance = Ceiling(
            300, preliminaryType.Id, "Wrong instance name");
        var info = new RevitFileInfo();
        info.Elements.AddRange([serializedType, preliminaryType, instance]);
        AddTypeParameters(instance, preliminaryType.Id);
        var state = new ParsingState();
        state.NativeParams[(uint)instance.Id] =
            CeilingNativeParams(instance.Id, serializedType.Id);
        state.NativeParams[(uint)instance.Id].Fields["m_attrId"] =
            preliminaryType.Id;
        state.ElemClassIdx[(uint)instance.Id] = CeilingClassId;
        state.CeilingTypeNameSource[(uint)serializedType.Id] =
            (1, "Canonical ceiling type");

        ElementBuilder.PostProcess(info, state);

        Assert.Equal(preliminaryType.Id, instance.TypeId);
        Assert.Equal(preliminaryType.Id, instance.FamilyTypeId);
        foreach (int parameterId in new[] { -1002050, -1002000, -1002052 })
        {
            Assert.Equal(preliminaryType.Id, Assert.Single(
                instance.Parameters, parameter =>
                    parameter.Id == parameterId).Value);
        }
        Assert.Equal("Wrong serialized type name", serializedType.Name);
        Assert.Equal("Wrong preliminary type", preliminaryType.Name);
        Assert.Equal("Wrong preliminary type", instance.Name);
        Assert.Equal(
            "Canonical ceiling type", instance.ValidatedCeilingTypeReferenceName);
    }

    [Theory]
    [InlineData("missing-native-row")]
    [InlineData("missing-native-field")]
    [InlineData("wrong-owner")]
    [InlineData("wrong-class")]
    [InlineData("nonpositive-type")]
    [InlineData("self-type")]
    [InlineData("unresolved-type")]
    [InlineData("wrong-type-category")]
    public void PostProcess_InvalidOwnCeilingLinkDoesNotCanonicalizeType(
        string brokenValidation)
    {
        var serializedType = Ceiling(200, 0, "Serialized type");
        var preliminaryType = Ceiling(250, 0, "Preliminary type");
        var instance = Ceiling(300, preliminaryType.Id, "Instance");
        var info = new RevitFileInfo();
        info.Elements.AddRange([serializedType, preliminaryType, instance]);
        AddTypeParameters(instance, preliminaryType.Id);
        var state = new ParsingState();
        state.NativeParams[(uint)instance.Id] =
            CeilingNativeParams(instance.Id, serializedType.Id);
        state.ElemClassIdx[(uint)instance.Id] = CeilingClassId;

        switch (brokenValidation)
        {
            case "missing-native-row":
                state.NativeParams.Clear();
                break;
            case "missing-native-field":
                state.NativeParams[(uint)instance.Id] = new();
                break;
            case "wrong-owner":
                state.NativeParams[(uint)instance.Id] =
                    CeilingNativeParams(instance.Id + 1, serializedType.Id);
                break;
            case "wrong-class":
                state.ElemClassIdx[(uint)instance.Id] = 566;
                break;
            case "nonpositive-type":
                state.NativeParams[(uint)instance.Id] =
                    CeilingNativeParams(instance.Id, 0);
                break;
            case "self-type":
                state.NativeParams[(uint)instance.Id] =
                    CeilingNativeParams(instance.Id, instance.Id);
                break;
            case "unresolved-type":
                state.NativeParams[(uint)instance.Id] =
                    CeilingNativeParams(instance.Id, 999);
                break;
            case "wrong-type-category":
                serializedType.CategoryId = -2000032;
                serializedType.Category = "OST_Floors";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(brokenValidation));
        }

        ElementBuilder.PostProcess(info, state);

        Assert.Equal(preliminaryType.Id, instance.TypeId);
        foreach (int parameterId in new[] { -1002050, -1002000, -1002052 })
        {
            Assert.Equal(preliminaryType.Id, Assert.Single(
                instance.Parameters, parameter =>
                    parameter.Id == parameterId).Value);
        }
        Assert.Null(instance.SerializedCeilingTypeId);
        Assert.Null(instance.ValidatedCeilingTypeReferenceName);
    }

    [Theory]
    [InlineData("duplicate-occurrence")]
    [InlineData("duplicate-target")]
    [InlineData("roof-soffit-subclass")]
    public void SerializedCeilingTypeLink_FailsClosedOutsideExactUniqueClass642Group(
        string brokenValidation)
    {
        var serializedType = Ceiling(200, 0, "Serialized type");
        var instance = Ceiling(300, 250, "Instance");
        var elements = new List<Element> { serializedType, instance };
        var nativeParams = new Dictionary<uint,
            TfBlockDeserializer.NativeParams>
        {
            [(uint)instance.Id] = CeilingNativeParams(
                instance.Id, serializedType.Id),
        };
        var classIds = CeilingClassIds(instance.Id);

        if (brokenValidation == "duplicate-occurrence")
        {
            elements.Add(Ceiling(instance.Id, 250, "Duplicate occurrence"));
        }
        else if (brokenValidation == "duplicate-target")
        {
            elements.Add(Ceiling(serializedType.Id, 0, "Duplicate target"));
        }
        else if (brokenValidation == "roof-soffit-subclass")
        {
            classIds[(uint)instance.Id] = 3458;
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(brokenValidation));
        }

        ElementBuilder.ApplySerializedCeilingTypeLinks(
            elements, nativeParams, classIds);

        Assert.Null(instance.SerializedCeilingTypeId);
    }

    [Fact]
    public void PostProcess_ValidatedCeilingTypeSurvivesLateWrongCategoryTypeParameterOverride()
    {
        var type = Ceiling(200, 0, "Wrong parsed type");
        var instance = Ceiling(300, type.Id, "Wrong instance name");
        instance.Parameters.Add(new Parameter
        {
            Id = -1002050,
            StorageType = StorageType.ElementId,
            Value = 900,
        });
        var wrongCategoryTarget = new Element
        {
            Id = 900,
            CategoryId = -2000023,
            Category = "OST_Doors",
            Name = "Wrong door type",
        };
        var info = new RevitFileInfo();
        info.Elements.AddRange([type, instance, wrongCategoryTarget]);
        var state = new ParsingState();
        state.NativeParams[(uint)instance.Id] =
            CeilingNativeParams(instance.Id, type.Id);
        state.ElemClassIdx[(uint)instance.Id] = CeilingClassId;
        state.CeilingTypeNameSource[(uint)type.Id] =
            (1, "Canonical ceiling type");
        state.ElementNameTable[(uint)wrongCategoryTarget.Id] =
            "Wrong late table name";

        ElementBuilder.PostProcess(info, state);

        Assert.Equal(type.Id, instance.TypeId);
        Assert.Equal("Canonical ceiling type", type.Name);
        Assert.Equal("Canonical ceiling type", instance.Name);
    }

    [Fact]
    public void PostProcess_BaselineCeilingTypeNamePropagationStillReachesSibling()
    {
        var type = Ceiling(200, 0, "Existing type name");
        var validatedInstance = Ceiling(300, type.Id, "Existing validated name");
        var unvalidatedSibling = Ceiling(301, type.Id, "Existing sibling name");
        var info = new RevitFileInfo();
        info.Elements.AddRange([type, validatedInstance, unvalidatedSibling]);
        var state = new ParsingState();
        state.NativeParams[(uint)validatedInstance.Id] =
            CeilingNativeParams(validatedInstance.Id, type.Id);
        state.ElemClassIdx[(uint)validatedInstance.Id] = CeilingClassId;
        state.CeilingTypeNameSource[(uint)type.Id] =
            (1, "Canonical ceiling type");

        ElementBuilder.PostProcess(info, state);

        Assert.Equal("Canonical ceiling type", type.Name);
        Assert.Equal("Canonical ceiling type", validatedInstance.Name);
        Assert.Equal("Canonical ceiling type", unvalidatedSibling.Name);
        Assert.Equal(type.Id, validatedInstance.SerializedCeilingTypeId);
        Assert.Equal(
            "Canonical ceiling type",
            validatedInstance.ValidatedCeilingTypeReferenceName);
        Assert.Null(unvalidatedSibling.SerializedCeilingTypeId);
        Assert.Null(unvalidatedSibling.ValidatedCeilingTypeReferenceName);
    }

    [Theory]
    [InlineData("wrong-instance-category")]
    [InlineData("wrong-type-category")]
    [InlineData("self-type")]
    [InlineData("unresolved-type")]
    [InlineData("missing-source")]
    [InlineData("multiple-blocks")]
    [InlineData("missing-terminal-name")]
    public void ApplyValidatedCeilingTypeNames_FailsClosedWithoutCompleteValidation(
        string brokenValidation)
    {
        var type = Ceiling(200, 0, "Existing ceiling type name");
        var instance = Ceiling(300, type.Id, "Existing ceiling instance name");
        var elements = new List<Element> { type, instance };
        var source = new Dictionary<uint,
            (int BlockCount, string? TerminalName)>
        {
            [(uint)type.Id] = (1, "Unvalidated replacement"),
        };
        switch (brokenValidation)
        {
            case "wrong-instance-category":
                instance.CategoryId = -2000032;
                break;
            case "wrong-type-category":
                type.CategoryId = -2000032;
                break;
            case "self-type":
                instance.TypeId = instance.Id;
                source[(uint)instance.Id] = (1, "Unvalidated replacement");
                break;
            case "unresolved-type":
                instance.TypeId = 999;
                break;
            case "missing-source":
                source.Clear();
                break;
            case "multiple-blocks":
                source[(uint)type.Id] = (2, null);
                break;
            case "missing-terminal-name":
                source[(uint)type.Id] = (1, null);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(brokenValidation));
        }

        ElementBuilder.ApplyValidatedCeilingTypeNames(
            elements, elements.ToDictionary(element => element.Id), source);

        Assert.Equal("Existing ceiling type name", type.Name);
        Assert.Equal("Existing ceiling instance name", instance.Name);
    }

    private static TfBlockDeserializer.NativeParams CeilingNativeParams(
        int ownerId, int typeId,
        string fieldName = "m_ceilingAttributesId")
    {
        var nativeParams = new TfBlockDeserializer.NativeParams();
        nativeParams.Fields["m_id"] = ownerId;
        nativeParams.Fields[fieldName] = typeId;
        return nativeParams;
    }

    private static Dictionary<uint, ushort> CeilingClassIds(int instanceId) =>
        new() { [(uint)instanceId] = CeilingClassId };

    private static void AddTypeParameters(Element element, int typeId)
    {
        foreach (int parameterId in new[] { -1002050, -1002000, -1002052 })
        {
            element.Parameters.Add(new Parameter
            {
                Id = parameterId,
                Definition = new Definition
                {
                    Id = parameterId,
                    Name = parameterId == -1002050
                        ? "ELEM_TYPE_PARAM"
                        : parameterId.ToString(),
                },
                StorageType = StorageType.ElementId,
                Value = typeId,
            });
        }
    }

    private static (byte[] Block, uint DataSize) CreateCeilingBlock(
        int occurrenceId, int ceilingTypeId)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(
            payload, System.Text.Encoding.Unicode, leaveOpen: true);

        // Element base.
        for (int index = 0; index < 6; index++) writer.Write(0u);
        writer.Write(0u); // m_constrInfo
        writer.Write(0u); // m_cellList
        writer.Write(0u); // m_docAccess
        writer.Write(occurrenceId); // m_id
        for (int index = 0; index < 7; index++) writer.Write(0);
        writer.Write((byte)0); // m_locked
        writer.Write((byte)0); // m_moribund
        writer.Write((byte)0); // m_dummy

        // HostObj + CeilingAndFloor + Ceiling.
        writer.Write(0u); // m_hostObjMiscData
        writer.Write(0u); // m_oGeomToElemMap
        writer.Write((byte)1); // m_roomBounding
        writer.Write(0u); // m_pRefFaces
        writer.Write(ceilingTypeId); // m_ceilingAttributesId
        writer.Write(0u); // m_oCeilingGrid

        byte[] fields = payload.ToArray();
        uint dataSize = checked((uint)fields.Length + 2);
        byte[] block = new byte[fields.Length + 18];
        BitConverter.GetBytes(occurrenceId).CopyTo(block, 0);
        BitConverter.GetBytes(dataSize).CopyTo(block, 8);
        BitConverter.GetBytes(CeilingClassId).CopyTo(block, 12);
        fields.CopyTo(block, 14);
        BitConverter.GetBytes(dataSize).CopyTo(block, block.Length - 4);
        return (block, dataSize);
    }

    private static Element Ceiling(int id, int typeId, string name) => new()
    {
        Id = id,
        TypeId = typeId,
        CategoryId = CeilingsCategoryId,
        Category = "OST_Ceilings",
        Name = name,
    };
}

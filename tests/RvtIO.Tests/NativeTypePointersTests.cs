using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class NativeTypePointersTests
{
    private static TfBlockDeserializer.NativeParams With(string key, int value)
    {
        var parameters = new TfBlockDeserializer.NativeParams();
        parameters.Fields[key] = value;
        return parameters;
    }

    [Theory]
    [InlineData(2335, "160:m_styleSymbolId")]     // linear dimension: its dimension style
    [InlineData(3681, "3681:m_typeId")]            // stairs
    [InlineData(485, "485:m_stairsRailingAttrId")] // railing
    [InlineData(3475, "3475:m_symbolId")]          // room tag
    [InlineData(527, "527:m_symbolId")]            // independent tag
    [InlineData(804, "804:m_typeId")]              // continuous footing
    [InlineData(3111, "3111:m_stairsAttrId")]      // stairs in the wide schema
    [InlineData(3989, "823:m_typeId")]             // continuous rail type
    public void TypeIdIsTheFieldOfTheRootClass(ushort rootClass, string key)
        => Assert.Equal(1234, NativeTypePointers.Of(rootClass, With(key, 1234)));

    [Fact]
    public void FieldOfAnotherClassIsNotAPointer()
        => Assert.Null(NativeTypePointers.Of(3681, With("160:m_styleSymbolId", 1234)));

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void UnsetFieldIsNoPointer(int value)
        => Assert.Null(NativeTypePointers.Of(2335, With("160:m_styleSymbolId", value)));

    [Fact]
    public void ClassWithoutPointerHasNone()
        => Assert.Null(NativeTypePointers.Of(9999, With("160:m_styleSymbolId", 1234)));

    [Fact]
    public void GroupMembershipCellOfAGroupInstanceIsItsTypeNotAMembership()
    {
        var cell = With("422:m_groupId", 988385);
        Assert.Equal(988385, NativeTypePointers.Of(1322, cell));
        Assert.Null(ElementBuilder.StoredGroupMembership(1322, cell));
        Assert.Equal(988385, ElementBuilder.StoredGroupMembership(1794, cell));
        Assert.Null(ElementBuilder.StoredGroupMembership(1794, With("422:m_groupId", -1)));
    }

    [Fact]
    public void EveryPointerFieldIsCapturedInProduction()
    {
        // The deserializer keeps only table and rule-input fields; the type pointers must be among them.
        foreach (string key in NativeTypePointers.ByClass.Values)
            Assert.True(TfBlockDeserializer.IsCapturedInProduction(key), key);
    }
}

using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public sealed class TmpMismatchUnit
{
    private static SchemaField Field(string name, byte type, byte mods = 0, int? reference = null) =>
        new(name, type, mods, 0, null, reference, null, null);

    [Fact]
    public void DetectsWrongDerivedClass()
    {
        // source: Holder owns a Base pointer holding a Derived (Derived : Base)
        var source = new SchemaDocument
        {
            Classes =
            [
                new(1, "Identifier", 0, 2, [Field("m_id64", 11)], []),
                new(2, "ElementId", 0, 1, [Field("m_id", 14, 0, 1)], []),
                new(10, "Base", 0, 1, [], []),
                new(11, "Derived", 10, 1, [], []),
                new(12, "Holder", 0, 1, [Field("m_child", 14, 1, 10)], []),
            ],
        };
        // target: Derived does NOT derive from Base (flat hierarchy)
        var target = new SchemaDocument
        {
            Classes =
            [
                new(1, "Identifier", 0, 1, [Field("m_id", 4)], []),
                new(2, "ElementId", 0, 1, [Field("m_id", 14, 0, 1)], []),
                new(20, "Base", 0, 1, [], []),
                new(21, "Derived", 0, 1, [], []),
                new(22, "Holder", 0, 1, [Field("m_child", 14, 1, 20)], []),
            ],
        };
        var transcoder = new R22Transcoder(source, target, 8);
        // Holder payload: root tag + handle + tag(Derived); the queued Derived object
        // has no fields, so nothing follows (its tag is consumed when the pointer is read)
        var body = new List<byte>();
        void U16(ushort v) { body.AddRange(BitConverter.GetBytes(v)); }
        void U32(uint v) { body.AddRange(BitConverter.GetBytes(v)); }
        void U64(ulong v) { body.AddRange(BitConverter.GetBytes(v)); }
        U16(12); U32(1); U16(11);
        var result = transcoder.Payload([.. body], 0, body.Count, out string? failure);
        Assert.True(result is not null, "payload null: " + failure);
        Assert.True(transcoder.PointerMismatches.Count > 0, "expected a mismatch");
    }
}

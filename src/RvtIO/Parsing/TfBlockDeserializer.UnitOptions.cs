using System.Text;

namespace RvtIO.Parsing;

internal static partial class TfBlockDeserializer
{
    internal sealed record UnitFormatOption(string Spec, string Symbol, string Unit, double Accuracy,
        int RoundingMethod, byte SuppressLeadingZeros, byte SuppressSpaces,
        byte SuppressTrailingZeros, byte UseDefault, byte UseGrouping, byte UsePlusPrefix);
    internal sealed record UnitOptions(UnitFormatOption[] Options, int UniversalUnitSystem,
        int DecimalSymbol, int DigitGroupingSymbol, int DigitGroupingAmount);
    internal sealed record UnitsElemOptions(uint OwnerId, int FamilyId, int DocumentAccess,
        int UnitsStart, int UnitsEnd, UnitOptions Value);

    private static readonly UnicodeEncoding UnitIdUtf16 = new(false, false, true);

    /// <summary>Complete own UnitsElem/AUnits root. Keeps every format field and the family/document ownership; does
    /// not choose the current document or a numeric coefficient. Accuracy is a display setting, not IFC rounding.</summary>
    internal static UnitsElemOptions? ExtractOwnUnitOptions(byte[] data, int start, uint size, ushort classId)
    {
        if (classId != 4029 || start < 0 || start > data.Length - 16 || size < 4 || size > int.MaxValue
            || (long)start + 16 + size > data.Length
            || BitConverter.ToUInt32(data, start + 8) != size
            || BitConverter.ToUInt16(data, start + 12) != classId
            || BitConverter.ToUInt32(data, checked(start + 12 + (int)size)) != size
            || !UnitOptionSchemasMatch()) return null;
        var fields = SerializationFields(classId);
        if (fields == null || !HasExactSchemaField(fields, 4029, "m_units", 14, 0, 82)) return null;
        var cursor = new Cursor
        {
            D = data, Pos = start + 14, End = checked(start + 12 + (int)size), RootClassId = classId,
        };
        var queue = new List<(ushort Cls, uint Id)>();
        var root = new NativeParams();
        UnitOptions? units = null;
        int unitsStart = -1, unitsEnd = -1;
        try
        {
            foreach (var field in fields)
            {
                if (field.Declaring == 4029 && field.Name == "m_units")
                {
                    if (units != null) return null;
                    unitsStart = cursor.Pos;
                    if (!TryReadUnitOptions(data, unitsStart, cursor.End, out unitsEnd, out units)) return null;
                    cursor.Pos = unitsEnd;
                }
                else ReadField(cursor, field, queue, 0, root);
                if (queue.Count > 100_000) return null;
            }
            for (int index = 0; index < queue.Count; index++)
            {
                if (queue.Count > 100_000) return null;
                ReadDeferredObject(cursor, queue[index].Cls, queue);
            }
        }
        catch (DecodeFail) { return null; }
        if (units == null || cursor.Pos != cursor.End
            || !root.Fields.TryGetValue("m_id", out int owner) || owner <= 0
            || (uint)owner != BitConverter.ToUInt32(data, start)
            || !root.Fields.TryGetValue("m_famId", out int family)
            || !root.Fields.TryGetValue("m_docAccess", out int document)) return null;
        return new((uint)owner, family, document, unitsStart - start, unitsEnd - start, units);
    }

    internal static bool TryReadUnitOptions(byte[] data, int start, int end, out int finish, out UnitOptions? units)
    {
        finish = -1; units = null;
        if (start < 0 || end < 0 || end > data.Length || start > end - 4) return false;
        int position = start;
        uint count = BitConverter.ToUInt32(data, position); position += 4;
        if (count == uint.MaxValue) count = 0;
        // Three counted strings plus an 18-byte option tail; four trailing i32s.
        if (count > 100_000 || count * 30L + 16 > end - position) return false;
        var options = new UnitFormatOption[(int)count];
        var specs = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < options.Length; index++)
        {
            if (!ReadUnitId(data, ref position, end, out string spec)
                || !specs.Add(spec)
                || ReadUnitFormatOption(data, ref position, end, spec) is not { } option) return false;
            options[index] = option;
        }
        if (position > end - 16) return false;
        units = new(options, BitConverter.ToInt32(data, position), BitConverter.ToInt32(data, position + 4),
            BitConverter.ToInt32(data, position + 8), BitConverter.ToInt32(data, position + 12));
        finish = position + 16;
        return true;
    }

    private static bool ReadUnitId(byte[] data, ref int position, int end, out string value)
    {
        value = "";
        if (position > end - 4) return false;
        uint length = BitConverter.ToUInt32(data, position); position += 4;
        if (length > 10_000 || length * 2L > end - position) return false;
        try { value = UnitIdUtf16.GetString(data, position, (int)length * 2); }
        catch (DecoderFallbackException) { return false; }
        position += (int)length * 2;
        return true;
    }

    private static UnitFormatOption? ReadUnitFormatOption(byte[] data, ref int position, int end, string spec)
    {
        if (!ReadUnitId(data, ref position, end, out string symbol)
            || !ReadUnitId(data, ref position, end, out string unit) || position > end - 18) return null;
        double accuracy = BitConverter.ToDouble(data, position);
        if (!double.IsFinite(accuracy) || accuracy < 0) return null;
        for (int flag = 12; flag < 18; flag++)
            if (data[position + flag] > 1) return null;
        var option = new UnitFormatOption(spec, symbol, unit, accuracy, BitConverter.ToInt32(data, position + 8),
            data[position + 12], data[position + 13], data[position + 14],
            data[position + 15], data[position + 16], data[position + 17]);
        position += 18;
        return option;
    }

    private static bool UnitOptionSchemasMatch()
    {
        if (!Schemas.TryGetValue(4029, out var element) || element.Name != "UnitsElem" || element.Base != 37
            || !Schemas.TryGetValue(82, out var units) || units.Name != "AUnits" || units.Fields.Count != 5
            || units.Fields[0] is not { Name: "m_formatOptionsMap", Type: 14, Mods: 80, Ref: null }
            || !Schemas.TryGetValue(83, out var pair) || pair.Name != "std::pair< ForgeTypeId, FormatOptions >"
            || pair.Fields.Count != 2 || pair.Fields[0] is not { Name: "first", Type: 14, Mods: 0, Ref: 45 }
            || pair.Fields[1] is not { Name: "second", Type: 14, Mods: 0, Ref: null }
            || !Schemas.TryGetValue(45, out var forge) || forge.Name != "ForgeTypeId" || forge.Fields.Count != 1
            || forge.Fields[0] is not { Name: "m_typeId", Type: 8, Mods: 96 }
            || !Schemas.TryGetValue(84, out var format) || format.Name != "FormatOptions" || format.Fields.Count != 10)
            return false;
        string[] tail = ["m_universalUnitSystem", "m_decimalSymbol", "m_digitGroupingSymbol", "m_digitGroupingAmount"];
        for (int index = 0; index < tail.Length; index++)
            if (units.Fields[index + 1].Name != tail[index] || units.Fields[index + 1].Type != 4
                || units.Fields[index + 1].Mods != 0) return false;
        string[] names = ["m_symbolTypeId", "m_unitTypeId", "m_accuracy", "m_roundingMethod",
            "m_bSuppressLeadingZeros", "m_bSuppressSpaces", "m_bSuppressTrailingZeros", "m_bUseDefault",
            "m_bUseGrouping", "m_bUsePlusPrefix"];
        for (int index = 0; index < names.Length; index++)
        {
            var field = format.Fields[index];
            byte type = index < 2 ? (byte)14 : index == 2 ? (byte)7 : index == 3 ? (byte)4 : (byte)1;
            if (field.Name != names[index] || field.Type != type || field.Mods != 0
                || index < 2 && field.Ref != 45) return false;
        }
        return true;
    }
}

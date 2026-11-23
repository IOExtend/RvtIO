using System.Buffers.Binary;
using System.Text;
using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using static RvtIO.Writing.UserParameterWriter;
using FormulaValue = RvtIO.Writing.ParameterFormulaEvaluator.Value;
using ValueKind = RvtIO.Writing.ParameterFormulaEvaluator.ValueKind;

namespace RvtIO.Writing;

internal readonly record struct FormulaParameterStorage(StorageType Storage, UnitType Unit, ValueKind Kind,
    int Min = int.MinValue, int Max = int.MaxValue)
{
    // Revit uses scalar numbers for Currency and Slope in formulas and keeps their specs for associations.
    internal UnitType FormulaUnit => Unit is UnitType.UT_Currency or UnitType.UT_Slope ? UnitType.UT_Number : Unit;

    internal static FormulaParameterStorage Read(WideBlockWalker walker, WideBlockWalker.Walk walk, int slot,
        bool includeCurrency = false)
    {
        if (walk.EmptyEntityAt >= 0)
            throw ParameterWriter.Unverified("A formula parameter contains unverified stored data.");
        return walker.Class(walk.Queue[slot])?.Name switch
        {
            "ParamDefValue" when Integer(walk, "ParamDefValue.m_restriction", slot) == 1 &&
                Integer(walk, "ParamDefValue.m_boundless", slot) == 0 &&
                ParameterMeasurement.Spec(walk, slot, includeCurrency: includeCurrency, includeAdditional: true) is UnitType unit
                => new(StorageType.Double, unit, ValueKind.Number),
            "ParamDefInt" when Integer(walk, "ParamDefInt.m_lowBound", slot) is >= int.MinValue and <= int.MaxValue and long low &&
                Integer(walk, "ParamDefInt.m_upBound", slot) is >= int.MinValue and <= int.MaxValue and long high && low <= high
                => new(StorageType.Integer, UnitType.UT_Number, ValueKind.Number, (int)low, (int)high),
            "ParamDefYesNo" => new(StorageType.Integer, UnitType.UT_Number, ValueKind.Boolean, 0, 1),
            "ParamDefString" => new(StorageType.String, UnitType.UT_Number, ValueKind.Text),
            _ => throw ParameterWriter.Unverified("A referenced parameter does not have a verified formula type."),
        };
    }

    internal FormulaValue Decode(ReadOnlySpan<byte> bytes)
    {
        if (Storage == StorageType.Double)
        {
            double value = BinaryPrimitives.ReadDoubleLittleEndian(bytes);
            if (!double.IsFinite(value))
                throw ParameterWriter.Unverified("A stored formula number is not finite.");
            return new(value, FormulaUnit);
        }
        if (Storage == StorageType.String)
            return FormulaValue.String(Encoding.Unicode.GetString(bytes[sizeof(uint)..]));
        int number = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (number < Min || number > Max)
            throw ParameterWriter.Unverified("A stored integer is outside the verified bounds for its formula type.");
        return new(number, Unit, Kind);
    }

    internal byte[] Encode(FormulaValue value)
    {
        if (value.Kind != Kind || value.Unit != FormulaUnit)
            throw ParameterWriter.Unverified("A formula result has incompatible storage or units.");
        if (Storage == StorageType.Double)
            return ParameterWriter.EncodeDouble(value.Number);
        if (Storage == StorageType.String)
        {
            string text = value.Text ?? throw ParameterWriter.Unverified("The text formula does not have a verified value.");
            ParameterWriter.ValidateText(text);
            return ParameterWriter.EncodeText(text);
        }
        double number = ParameterFormulaEvaluator.Round(value.Number);
        if (number < Min || number > Max)
            throw new RvtWriteRefusedException("invalid-parameter-value", "A recalculated integer exceeds its definition bounds.");
        byte[] bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, (int)number);
        return bytes;
    }
}

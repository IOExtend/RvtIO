using RvtIO.BuiltIns;
using RvtIO.Parsing;

namespace RvtIO.Writing;

internal static class ParameterFormulaEvaluator
{
    // The same bounded recursion covers both expression nodes and references to other global parameters.
    internal const int MaxDepth = 40;
    private const double ComparisonTolerance = 1e-9;
    // Revit retains the current result when an exponentiation would exceed this magnitude.
    private const double MaximumExponentialResult = 1e30;
    // Revit's pi() is stored with this precision, including when it is multiplied by another parameter.
    private const double RevitPi = 3.14159265358979;
    private const string Arguments = WideBlockWalker.AllItemsKeyPrefix + "FunctionExpression.m_subexpressions";
    internal enum ValueKind { Number, Boolean, Text }
    internal readonly record struct Value(double Number, UnitType Unit = UnitType.UT_Number, ValueKind Kind = ValueKind.Number,
        string? Text = null)
    {
        internal static Value Boolean(bool value) => new(value ? 1 : 0, Kind: ValueKind.Boolean);
        internal static Value String(string value) => new(0, Kind: ValueKind.Text, Text: value);
    }
    internal readonly record struct Linear(long InputId, double Scale, Value Offset, bool ExactScaling)
    {
        internal static Linear Input(long id, UnitType unit) => new(id, 1, new(0, unit), true);
    }

    private enum BinaryOperator { Add = 1, Subtract = 2, Multiply = 3, Divide = 4, RaiseToPower = 5, Equal = 6, Greater = 7, Less = 8 }
    private enum Function : long
    {
        Sin = 1, Cos = 2, Tan = 3, Asin = 4, Acos = 5, Atan = 6, Exp = 7, Log = 8, Sqrt = 9,
        If = 10, Or = 11, And = 12, Not = 13, Abs = 14, Pi = 16, Round = 18, RoundUp = 19, RoundDown = 20, Ln = 22,
    }

    internal static string? CaptureKey(SchemaClass declaring, SchemaField field) => (declaring.Name, field.Name) switch
    {
        ("BinaryOperatorExpression", "m_binaryOperator" or "m_pLeftSubexpression" or "m_pRightSubexpression") or
        ("UnaryOperatorExpression", "m_unaryOperator" or "m_pSubexpression") or
        ("ParenExpression", "m_pSubexpression") or ("NumberConstantExpression", "m_value" or "m_unitType" or "m_specTypeId") or
        ("StringConstantExpression", "m_value") or ("FunctionExpression", "m_function")
            => declaring.Name + "." + field.Name,
        ("FunctionExpression", "m_subexpressions") => Arguments,
        _ => null,
    };

    // A null result means the current inputs make Revit keep the formula's stored value.
    internal static Value? Evaluate(WideBlockWalker walker, WideBlockWalker.Walk walk, WideBlockWalker.Capture expression,
        int depth, Func<long, int, Value> resolve)
    {
        return Visit(ChildSlot(expression, walk), depth);

        Value? Visit(int slot, int remaining)
        {
            if (remaining <= 0)
                throw ParameterWriter.Unverified("The formula dependency depth exceeds the verified limit.");

            switch (walker.Class(walk.Queue[slot])?.Name)
            {
                case "ParameterExpression":
                    long id = Field(slot, "formula-reference", WideBlockWalker.CaptureKind.Integer).Integer;
                    return resolve(id, remaining - 1);
                case "NumberConstantExpression":
                    return ReadNumber(walk, slot);
                case "StringConstantExpression":
                    string text = Field(slot, "StringConstantExpression.m_value", WideBlockWalker.CaptureKind.Text).Text ?? throw Unsupported();
                    return Value.String(text);
                case "ParenExpression":
                    return Visit(Child(slot, "ParenExpression.m_pSubexpression"), remaining - 1);
                case "UnaryOperatorExpression":
                    if (Field(slot, "UnaryOperatorExpression.m_unaryOperator", WideBlockWalker.CaptureKind.Integer).Integer != 1)
                        throw Unsupported();
                    if (Visit(Child(slot, "UnaryOperatorExpression.m_pSubexpression"), remaining - 1) is not { } operand)
                        return null;
                    if (operand.Kind != ValueKind.Number)
                        throw Unsupported();
                    return operand with { Number = -operand.Number };
                case "BinaryOperatorExpression":
                    long operation = Field(slot, "BinaryOperatorExpression.m_binaryOperator", WideBlockWalker.CaptureKind.Integer).Integer;
                    if (operation is < 1 or > 8)
                        throw Unsupported();
                    var left = Visit(Child(slot, "BinaryOperatorExpression.m_pLeftSubexpression"), remaining - 1);
                    var right = Visit(Child(slot, "BinaryOperatorExpression.m_pRightSubexpression"), remaining - 1);
                    return left is { } lhs && right is { } rhs
                        ? Calculate((BinaryOperator)operation, lhs, rhs, allowCompoundUnits: true) : null;
                case "FunctionExpression":
                    return EvaluateFunction(slot, remaining);
                default:
                    throw Unsupported();
            }
        }

        Value? EvaluateFunction(int slot, int remaining)
        {
            var function = (Function)Field(slot, "FunctionExpression.m_function", WideBlockWalker.CaptureKind.Integer).Integer;
            int arity = function switch
            {
                Function.Pi => 0,
                Function.If => 3,
                Function.And or Function.Or => 2,
                Function.Sin or Function.Cos or Function.Tan or Function.Asin or Function.Acos or Function.Atan or
                Function.Exp or Function.Log or Function.Ln or Function.Sqrt or Function.Abs or Function.Not or
                Function.Round or Function.RoundUp or Function.RoundDown => 1,
                _ => throw Unsupported(),
            };
            if (Field(slot, Arguments + "#", WideBlockWalker.CaptureKind.Integer).Integer != arity)
                throw Unsupported();
            if (function == Function.Pi)
                return new(RevitPi);
            int[] children = Enumerable.Range(0, arity).Select(i => Child(slot, Arguments + "@" + i)).ToArray();
            Value? Argument(int index) => Visit(children[index], remaining - 1);

            if (Argument(0) is not { } argument)
                return null;
            return function switch
            {
                Function.If => Argument(AsBoolean(argument) ? 1 : 2),
                Function.And when !AsBoolean(argument) => Value.Boolean(false),
                Function.Or when AsBoolean(argument) => Value.Boolean(true),
                Function.And or Function.Or => Argument(1) is { } right ? Value.Boolean(AsBoolean(right)) : null,
                Function.Not => Value.Boolean(!AsBoolean(argument)),
                _ when argument.Kind == ValueKind.Number => CalculateFunction(function, argument),
                _ => throw Unsupported(),
            };
        }

        int Child(int parent, string key) => ChildSlot(Field(parent, key, WideBlockWalker.CaptureKind.PointerClass), walk);

        WideBlockWalker.Capture Field(int slot, string key, WideBlockWalker.CaptureKind kind) =>
            ReadField(walk, slot, key, kind);
    }

    internal static Linear ReadLinear(WideBlockWalker walker, WideBlockWalker.Walk walk, WideBlockWalker.Capture expression,
        int depth, Func<long, int, Linear> resolve)
    {
        return Visit(ChildSlot(expression, walk), depth);

        Linear Visit(int slot, int remaining)
        {
            if (remaining <= 0)
                throw Unsupported();
            switch (walker.Class(walk.Queue[slot])?.Name)
            {
                case "ParameterExpression":
                    return resolve(ReadField(walk, slot, "formula-reference", WideBlockWalker.CaptureKind.Integer).Integer,
                        remaining - 1);
                case "NumberConstantExpression":
                    return new(0, 0, ReadNumber(walk, slot), true);
                case "ParenExpression":
                    return Visit(Child(slot, "ParenExpression.m_pSubexpression"), remaining - 1);
                case "UnaryOperatorExpression" when
                    ReadField(walk, slot, "UnaryOperatorExpression.m_unaryOperator", WideBlockWalker.CaptureKind.Integer).Integer == 1:
                    var operand = Visit(Child(slot, "UnaryOperatorExpression.m_pSubexpression"), remaining - 1);
                    return operand with { Scale = -operand.Scale, Offset = operand.Offset with { Number = -operand.Offset.Number } };
                case "BinaryOperatorExpression":
                    var operation = (BinaryOperator)ReadField(walk, slot, "BinaryOperatorExpression.m_binaryOperator",
                        WideBlockWalker.CaptureKind.Integer).Integer;
                    var left = Visit(Child(slot, "BinaryOperatorExpression.m_pLeftSubexpression"), remaining - 1);
                    var right = Visit(Child(slot, "BinaryOperatorExpression.m_pRightSubexpression"), remaining - 1);
                    if (operation is < BinaryOperator.Add or > BinaryOperator.Divide ||
                        right.InputId != 0 && (left.InputId != 0 || operation == BinaryOperator.Divide))
                        throw Unsupported();

                    // Direct assignments to formula results have separate verification requirements.
                    var offset = Calculate(operation, left.Offset, right.Offset, allowCompoundUnits: false) ?? throw Unsupported();
                    if (left.InputId == 0 && right.InputId == 0)
                        return new(0, 0, offset, true);

                    var (variable, constant) = left.InputId != 0 ? (left, right) : (right, left);
                    double factor = constant.Offset.Number;
                    double scale = operation switch
                    {
                        BinaryOperator.Add => variable.Scale,
                        BinaryOperator.Subtract => left.InputId != 0 ? variable.Scale : -variable.Scale,
                        BinaryOperator.Multiply => variable.Scale * factor,
                        BinaryOperator.Divide => variable.Scale / factor,
                        _ => throw Unsupported(),
                    };
                    if (!ExactBinaryCoefficient(scale) || !ExactBinaryCoefficient(offset.Number))
                        throw Unsupported();
                    bool exactScaling = variable.ExactScaling && (operation is BinaryOperator.Multiply or BinaryOperator.Divide
                        ? Math.ScaleB(1, Math.ILogB(Math.Abs(factor))) == Math.Abs(factor) : factor == 0);
                    return new(variable.InputId, scale, offset, exactScaling);
                default:
                    throw Unsupported();
            }
        }

        int Child(int parent, string key) => ChildSlot(ReadField(walk, parent, key, WideBlockWalker.CaptureKind.PointerClass), walk);

        // This range keeps affine evaluations on 32-bit integer inputs exact in the binary mantissa.
        static bool ExactBinaryCoefficient(double value)
        {
            double scaled = Math.ScaleB(value, 12);
            return Math.Abs(value) <= 256 && scaled == Math.Truncate(scaled);
        }
    }

    private static Value ReadNumber(WideBlockWalker.Walk walk, int slot)
    {
        double number = ReadField(walk, slot, "NumberConstantExpression.m_value", WideBlockWalker.CaptureKind.Real).Real;
        if (!double.IsFinite(number) ||
            ParameterMeasurement.Spec(walk, slot, "NumberConstantExpression", includeAdditional: true) is not { } unit)
            throw Unsupported();
        return new(number, unit);
    }

    private static WideBlockWalker.Capture ReadField(WideBlockWalker.Walk walk, int slot, string key,
        WideBlockWalker.CaptureKind kind) => walk.Captures.Where(c => c.QueueIndex == slot && c.Key == key).ToArray()
        is [var field] && field.Kind == kind ? field : throw Unsupported();

    internal static int ChildSlot(WideBlockWalker.Capture pointer, WideBlockWalker.Walk walk)
    {
        int child = pointer.QueuePosition;
        if (pointer.Kind != WideBlockWalker.CaptureKind.PointerClass || child < 0 || child <= pointer.QueueIndex ||
            child >= walk.Queue.Count || pointer.Integer <= 0 || walk.Queue[child] != pointer.Integer)
            throw Unsupported();
        return child;
    }

    private static bool AsBoolean(Value value) => value.Kind == ValueKind.Boolean ? value.Number != 0 : throw Unsupported();

    private static Value? CalculateFunction(Function function, Value argument)
    {
        double number = argument.Number;
        Value? result = (function, argument.Unit) switch
        {
            (Function.Abs, _) => argument with { Number = Math.Abs(number) },
            (Function.Sqrt, UnitType.UT_Number) when number >= 0 => new(Math.Sqrt(number)),
            (Function.Sqrt, UnitType.UT_Area) when number >= 0 => new(Math.Sqrt(number), UnitType.UT_Length),
            (Function.Exp, UnitType.UT_Number) => ExponentialResult(Math.Exp(number), UnitType.UT_Number),
            (Function.Log, UnitType.UT_Number) when number >= ComparisonTolerance => new(Math.Log10(number)),
            (Function.Ln, UnitType.UT_Number) when number >= ComparisonTolerance => new(Math.Log(number)),
            (Function.Sin, UnitType.UT_Angle) => new(Math.Sin(number)),
            (Function.Cos, UnitType.UT_Angle) => new(Math.Cos(number)),
            (Function.Tan, UnitType.UT_Angle) when Math.Abs(Math.Cos(number)) > ComparisonTolerance => new(Math.Tan(number)),
            (Function.Asin, UnitType.UT_Number) when Math.Abs(number) <= 1 => new(Math.Asin(number), UnitType.UT_Angle),
            (Function.Acos, UnitType.UT_Number) when Math.Abs(number) <= 1 => new(Math.Acos(number), UnitType.UT_Angle),
            (Function.Atan, UnitType.UT_Number) => new(Math.Atan(number), UnitType.UT_Angle),
            (Function.Round, UnitType.UT_Number) => new(Round(number)),
            (Function.RoundUp, UnitType.UT_Number) => new(RoundDirected(number, up: true)),
            (Function.RoundDown, UnitType.UT_Number) => new(RoundDirected(number, up: false)),
            (Function.Sqrt, UnitType.UT_Number or UnitType.UT_Area) or
            (Function.Log or Function.Ln or Function.Asin or Function.Acos, UnitType.UT_Number) or
            (Function.Tan, UnitType.UT_Angle) => null,
            _ => throw Unsupported(),
        };
        return result is { } calculated ? Finite(calculated) : null;
    }

    internal static double Round(double value)
    {
        double floor = Math.Floor(value);
        if (value == floor)
            return value;
        VerifyStableChange(value, floor + 0.5);
        return Math.Floor(value + 0.5);
    }

    private static double RoundDirected(double value, bool up)
    {
        VerifyStableChange(value, Math.Round(value));
        return up ? Math.Ceiling(value) : Math.Floor(value);
    }

    private static Value? Calculate(BinaryOperator operation, Value left, Value right, bool allowCompoundUnits)
    {
        if (left.Kind != ValueKind.Number || right.Kind != ValueKind.Number)
            throw Unsupported();
        if (operation is BinaryOperator.Equal or BinaryOperator.Greater or BinaryOperator.Less)
        {
            if (left.Unit != right.Unit)
                throw Unsupported();
            if (left.Number != right.Number)
                VerifyStableChange(left.Number, right.Number);
            return Value.Boolean(operation switch
            {
                BinaryOperator.Equal => left.Number == right.Number,
                BinaryOperator.Greater => left.Number > right.Number,
                _ => left.Number < right.Number,
            });
        }

        UnitType? unit = operation switch
        {
            BinaryOperator.Add or BinaryOperator.Subtract when left.Unit == right.Unit => left.Unit,
            BinaryOperator.Multiply when left.Unit == UnitType.UT_Number => right.Unit,
            BinaryOperator.Multiply or BinaryOperator.Divide when right.Unit == UnitType.UT_Number => left.Unit,
            BinaryOperator.Divide when left.Unit == right.Unit => UnitType.UT_Number,
            BinaryOperator.Multiply => LinearUnit(Power(left.Unit) + Power(right.Unit)),
            BinaryOperator.Divide => LinearUnit(Power(left.Unit) - Power(right.Unit)),
            BinaryOperator.RaiseToPower when right.Unit == UnitType.UT_Number => (left.Unit, right.Number) switch
            {
                (UnitType.UT_Number, _) => UnitType.UT_Number,
                (UnitType.UT_Length, 2) => UnitType.UT_Area,
                (UnitType.UT_Length, 3) => UnitType.UT_Volume,
                (UnitType.UT_Area, 0.5) => UnitType.UT_Length,
                _ => null,
            },
            _ => null,
        };
        if (unit is null && allowCompoundUnits)
            unit = CompoundUnit(operation, left.Unit, right.Unit);
        if (unit is null)
            throw Unsupported();

        if (operation == BinaryOperator.Divide && Math.Abs(right.Number) <= ComparisonTolerance)
            return null;
        if (operation == BinaryOperator.RaiseToPower)
            return RaiseToPower(left.Number, right.Number, unit.Value);
        return Finite(new(CalculateNumber(operation, left.Number, right.Number), unit.Value));
    }

    private static Value Finite(Value value)
    {
        if (!double.IsFinite(value.Number))
            throw new RvtWriteRefusedException("invalid-parameter-value", "The global assignment does not produce finite formula values.");
        return value;
    }

    internal static void VerifyStableChange(double before, double after)
    {
        if (BitConverter.DoubleToInt64Bits(before) == BitConverter.DoubleToInt64Bits(after))
            return;

        // Revit can suppress changes within its relative comparison tolerance, even after recomputing the expression.
        double magnitude = Math.Abs(before) + Math.Abs(after);
        if (!double.IsFinite(magnitude) || magnitude <= ComparisonTolerance ||
            Math.Abs(after - before) <= Math.BitIncrement(magnitude * ComparisonTolerance))
            throw ParameterWriter.Unverified("The numeric change cannot be verified against Revit's comparison tolerance.");
    }

    internal static bool IsWithinVerifiedRelativeTolerance(double before, double after)
    {
        double magnitude = Math.Abs(before) + Math.Abs(after);
        // Leave the values at the floating-point cutoff to the normal stability check.
        return double.IsFinite(magnitude) && magnitude > ComparisonTolerance &&
            Math.Abs(after - before) < Math.BitDecrement(magnitude * ComparisonTolerance);
    }

    private static UnitType? CompoundUnit(BinaryOperator operation, UnitType left, UnitType right) => (operation, left, right) switch
    {
        (BinaryOperator.Multiply, UnitType.UT_MassDensity, UnitType.UT_Volume) or
        (BinaryOperator.Multiply, UnitType.UT_Volume, UnitType.UT_MassDensity) => UnitType.UT_Mass,
        (BinaryOperator.Multiply, UnitType.UT_Speed, UnitType.UT_TimeInterval) or
        (BinaryOperator.Multiply, UnitType.UT_TimeInterval, UnitType.UT_Speed) => UnitType.UT_Length,
        (BinaryOperator.Divide, UnitType.UT_Mass, UnitType.UT_Volume) => UnitType.UT_MassDensity,
        (BinaryOperator.Divide, UnitType.UT_Mass, UnitType.UT_MassDensity) => UnitType.UT_Volume,
        (BinaryOperator.Divide, UnitType.UT_Length, UnitType.UT_TimeInterval) => UnitType.UT_Speed,
        (BinaryOperator.Divide, UnitType.UT_Length, UnitType.UT_Speed) => UnitType.UT_TimeInterval,
        _ => null,
    };

    private static int? Power(UnitType unit) => unit switch
    {
        UnitType.UT_Length => 1,
        UnitType.UT_Area => 2,
        UnitType.UT_Volume => 3,
        _ => null,
    };

    private static UnitType? LinearUnit(int? power) => power switch
    {
        1 => UnitType.UT_Length,
        2 => UnitType.UT_Area,
        3 => UnitType.UT_Volume,
        _ => null,
    };

    private static double CalculateNumber(BinaryOperator operation, double left, double right) => operation switch
    {
        BinaryOperator.Add => left + right,
        BinaryOperator.Subtract => left - right,
        BinaryOperator.Multiply => left * right,
        BinaryOperator.Divide => left / right,
        _ => throw Unsupported(),
    };

    private static Value? RaiseToPower(double number, double exponent, UnitType unit)
    {
        if (number == 0 && exponent > 0)
            return new(0, unit);
        if (Math.Abs(number) <= ComparisonTolerance)
            throw Unsupported();

        double value = Math.Pow(number, exponent);
        if (double.IsNaN(value))
            throw Unsupported();
        return value == 0 ? null : ExponentialResult(value, unit);
    }

    private static Value? ExponentialResult(double value, UnitType unit)
        => Math.Abs(value) <= MaximumExponentialResult ? new(value, unit) : null;

    private static RvtWriteRefusedException Unsupported() =>
        ParameterWriter.Unverified("A dependent formula has an expression or units outside the verified write scope.");
}

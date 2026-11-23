using RvtIO.BuiltIns;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;
using Value = RvtIO.Writing.ParameterFormulaEvaluator.Value;

namespace RvtIO.Tests;

/// <summary>The formula evaluator on synthetic expression trees: every operator, every function and the units and limits that decide
/// between a value, a retained stored value (null) and a refusal. Codes are those of the file format: binary operators 1 to 8,
/// functions by their stored number.</summary>
public sealed class ParameterFormulaEvaluatorTests
{
    private const string NumberSpec = "autodesk.spec.aec:number-2.0.0", LengthSpec = "autodesk.spec.aec:length-2.0.0",
        AreaSpec = "autodesk.spec.aec:area-2.0.0", VolumeSpec = "autodesk.spec.aec:volume-2.0.0", AngleSpec = "autodesk.spec.aec:angle-2.0.0",
        MassSpec = "autodesk.spec.aec.structural:mass-2.0.0", DensitySpec = "autodesk.spec.aec:massDensity-2.0.0",
        SpeedSpec = "autodesk.spec.aec:speed-2.0.0", TimeSpec = "autodesk.spec.aec:time-2.0.0";
    private const long Add = 1, Subtract = 2, Multiply = 3, Divide = 4, Power = 5, Equal = 6, Greater = 7, Less = 8;
    private const long Sin = 1, Cos = 2, Tan = 3, Asin = 4, Acos = 5, Atan = 6, Exp = 7, Log = 8, Sqrt = 9, If = 10, Or = 11, And = 12,
        Not = 13, Abs = 14, Pi = 16, Round = 18, RoundUp = 19, RoundDown = 20, Ln = 22;

    private abstract record Node;
    private sealed record Num(double Value, string Spec = NumberSpec) : Node;
    private sealed record Str(string Text) : Node;
    private sealed record Paren(Node Child) : Node;
    private sealed record Unary(long Operator, Node Child) : Node;
    private sealed record Binary(long Operator, Node Left, Node Right) : Node;
    private sealed record Fn(long Function, Node[] Arguments, long? Count = null) : Node;
    private sealed record Ref(long Id) : Node;
    private sealed record Other : Node;

    private static Fn Call(long function, params Node[] arguments) => new(function, arguments);
    private static readonly Node True = new Binary(Greater, new Num(1), new Num(0)), False = new Binary(Less, new Num(1), new Num(0));
    private static readonly string[] ClassNames =
        ["ParameterExpression", "NumberConstantExpression", "StringConstantExpression", "ParenExpression", "UnaryOperatorExpression",
            "BinaryOperatorExpression", "FunctionExpression", "SomethingElse"];
    private static readonly WideBlockWalker Walker = new(new SchemaDocument
    {
        Classes = [.. ClassNames.Select((name, index) => new SchemaClass(index + 1, name, 0, 1, [], []))],
    }, (_, _) => null, []);
    private const string Arguments = WideBlockWalker.AllItemsKeyPrefix + "FunctionExpression.m_subexpressions";

    private static Value? Evaluate(Node root, Func<long, int, Value>? resolve = null, int depth = ParameterFormulaEvaluator.MaxDepth)
    {
        var walk = new WideBlockWalker.Walk();
        int Emit(Node node)
        {
            int slot = walk.Queue.Count;
            walk.Queue.Add(node switch
            {
                Ref => 1, Num => 2, Str => 3, Paren => 4, Unary => 5, Binary => 6, Fn => 7, _ => 8,
            });
            void Integer(string key, long value) =>
                walk.Captures.Add(new(key, WideBlockWalker.CaptureKind.Integer, value, 0, null, false, slot, "x", key));
            void Child(string key, Node child)
            {
                int at = Emit(child);
                walk.Captures.Add(new(key, WideBlockWalker.CaptureKind.PointerClass, walk.Queue[at], 0, null, false, slot, "x", key,
                    QueuePosition: at));
            }
            switch (node)
            {
                case Num number:
                    walk.Captures.Add(new("NumberConstantExpression.m_value", WideBlockWalker.CaptureKind.Real, 0, number.Value, null, false,
                        slot, "NumberConstantExpression", "m_value"));
                    walk.Captures.Add(new("NumberConstantExpression.m_specTypeId", WideBlockWalker.CaptureKind.Text, 0, 0, number.Spec, false,
                        slot, "NumberConstantExpression", "m_specTypeId"));
                    break;
                case Str text:
                    walk.Captures.Add(new("StringConstantExpression.m_value", WideBlockWalker.CaptureKind.Text, 0, 0, text.Text, false,
                        slot, "StringConstantExpression", "m_value"));
                    break;
                case Paren paren:
                    Child("ParenExpression.m_pSubexpression", paren.Child);
                    break;
                case Unary unary:
                    Integer("UnaryOperatorExpression.m_unaryOperator", unary.Operator);
                    Child("UnaryOperatorExpression.m_pSubexpression", unary.Child);
                    break;
                case Binary binary:
                    Integer("BinaryOperatorExpression.m_binaryOperator", binary.Operator);
                    Child("BinaryOperatorExpression.m_pLeftSubexpression", binary.Left);
                    Child("BinaryOperatorExpression.m_pRightSubexpression", binary.Right);
                    break;
                case Fn function:
                    Integer("FunctionExpression.m_function", function.Function);
                    Integer(Arguments + "#", function.Count ?? function.Arguments.Length);
                    for (int i = 0; i < function.Arguments.Length; i++)
                        Child(Arguments + "@" + i, function.Arguments[i]);
                    break;
                case Ref reference:
                    Integer("formula-reference", reference.Id);
                    break;
            }
            return slot;
        }

        int rootSlot = Emit(root);
        var pointer = new WideBlockWalker.Capture("root", WideBlockWalker.CaptureKind.PointerClass, walk.Queue[rootSlot], 0, null, true, -1,
            "Root", "root", QueuePosition: rootSlot);
        return ParameterFormulaEvaluator.Evaluate(Walker, walk, pointer, depth,
            resolve ?? ((_, _) => throw new InvalidOperationException("no reference expected")));
    }

    private static Value Calculate(Node root) => Evaluate(root) ?? throw new Xunit.Sdk.XunitException("The expression retained the stored value.");

    private static void AssertValue(double expected, UnitType unit, Value actual)
    {
        Assert.Equal(ParameterFormulaEvaluator.ValueKind.Number, actual.Kind);
        Assert.Equal(unit, actual.Unit);
        Assert.Equal(expected, actual.Number, Math.Abs(expected) * 1e-12);
    }

    private static void AssertRefused(Node root, string kind = "unverified-parameter-storage") =>
        Assert.Equal(kind, Assert.Throws<RvtWriteRefusedException>(() => Evaluate(root)).Kind);

    [Theory]
    [InlineData(Add, 2, 3, 5)]
    [InlineData(Subtract, 2, 3, -1)]
    [InlineData(Multiply, 2, 3, 6)]
    [InlineData(Divide, 6, 3, 2)]
    [InlineData(Power, 2, 3, 8)]
    [InlineData(Power, 4, 0.5, 2)]
    [InlineData(Power, 2, -1, 0.5)]
    public void CalculatesTheArithmeticOperators(long operation, double left, double right, double expected) =>
        AssertValue(expected, UnitType.UT_Number, Calculate(new Binary(operation, new Num(left), new Num(right))));

    [Theory]
    [InlineData(Equal, 2, 2, true)]
    [InlineData(Equal, 2, 3, false)]
    [InlineData(Equal, 3, 2, false)]
    [InlineData(Greater, 3, 2, true)]
    [InlineData(Greater, 2, 3, false)]
    [InlineData(Greater, 2, 2, false)]
    [InlineData(Less, 2, 3, true)]
    [InlineData(Less, 3, 2, false)]
    [InlineData(Less, 2, 2, false)]
    public void ComparesNumbers(long operation, double left, double right, bool expected)
    {
        var value = Calculate(new Binary(operation, new Num(left), new Num(right)));
        Assert.Equal(ParameterFormulaEvaluator.ValueKind.Boolean, value.Kind);
        Assert.Equal(expected, value.Number != 0);
    }

    [Fact]
    public void RefusesAComparisonThatRevitMaySuppressAndAnyOtherOperator()
    {
        AssertRefused(new Binary(Greater, new Num(1), new Num(1.0000000000001)));
        AssertRefused(new Binary(Less, new Num(1, LengthSpec), new Num(2)));
        AssertRefused(new Binary(9, new Num(1), new Num(2)));
        AssertRefused(new Binary(0, new Num(1), new Num(2)));
    }

    [Fact]
    public void NegatesAndGroupsAndReadsConstants()
    {
        AssertValue(-2, UnitType.UT_Number, Calculate(new Unary(1, new Num(2))));
        AssertValue(-2, UnitType.UT_Length, Calculate(new Unary(1, new Num(2, LengthSpec))));
        AssertValue(10, UnitType.UT_Number, Calculate(new Binary(Multiply, new Paren(new Binary(Add, new Num(2), new Num(3))), new Num(2))));
        var text = Calculate(new Str("abc"));
        Assert.Equal(ParameterFormulaEvaluator.ValueKind.Text, text.Kind);
        Assert.Equal("abc", text.Text);
        AssertRefused(new Unary(2, new Num(2)));
        AssertRefused(new Unary(1, new Str("abc")));
    }

    [Theory]
    [InlineData(Multiply, NumberSpec, LengthSpec, UnitType.UT_Length)]
    [InlineData(Multiply, LengthSpec, NumberSpec, UnitType.UT_Length)]
    [InlineData(Multiply, LengthSpec, LengthSpec, UnitType.UT_Area)]
    [InlineData(Multiply, LengthSpec, AreaSpec, UnitType.UT_Volume)]
    [InlineData(Multiply, AreaSpec, LengthSpec, UnitType.UT_Volume)]
    [InlineData(Divide, LengthSpec, NumberSpec, UnitType.UT_Length)]
    [InlineData(Divide, LengthSpec, LengthSpec, UnitType.UT_Number)]
    [InlineData(Divide, AreaSpec, LengthSpec, UnitType.UT_Length)]
    [InlineData(Divide, VolumeSpec, LengthSpec, UnitType.UT_Area)]
    [InlineData(Divide, VolumeSpec, AreaSpec, UnitType.UT_Length)]
    [InlineData(Add, LengthSpec, LengthSpec, UnitType.UT_Length)]
    [InlineData(Subtract, AreaSpec, AreaSpec, UnitType.UT_Area)]
    [InlineData(Multiply, DensitySpec, VolumeSpec, UnitType.UT_Mass)]
    [InlineData(Multiply, VolumeSpec, DensitySpec, UnitType.UT_Mass)]
    [InlineData(Multiply, SpeedSpec, TimeSpec, UnitType.UT_Length)]
    [InlineData(Multiply, TimeSpec, SpeedSpec, UnitType.UT_Length)]
    [InlineData(Divide, MassSpec, VolumeSpec, UnitType.UT_MassDensity)]
    [InlineData(Divide, MassSpec, DensitySpec, UnitType.UT_Volume)]
    [InlineData(Divide, LengthSpec, TimeSpec, UnitType.UT_Speed)]
    [InlineData(Divide, LengthSpec, SpeedSpec, UnitType.UT_TimeInterval)]
    public void CombinesUnitsOfTheOperands(long operation, string left, string right, UnitType unit)
    {
        var value = Calculate(new Binary(operation, new Num(6, left), new Num(3, right)));
        Assert.Equal(unit, value.Unit);
        Assert.Equal(operation switch { Add => 9, Subtract => 3, Multiply => 18, _ => 2 }, value.Number);
    }

    [Theory]
    [InlineData(LengthSpec, 2, UnitType.UT_Area)]
    [InlineData(LengthSpec, 3, UnitType.UT_Volume)]
    [InlineData(AreaSpec, 0.5, UnitType.UT_Length)]
    [InlineData(NumberSpec, 2, UnitType.UT_Number)]
    public void RaisesToAPowerKeepingTheUnitTheResultHas(string spec, double exponent, UnitType unit) =>
        Assert.Equal(unit, Calculate(new Binary(Power, new Num(4, spec), new Num(exponent))).Unit);

    [Theory]
    [InlineData(Add, LengthSpec, NumberSpec)]
    [InlineData(Add, LengthSpec, AreaSpec)]
    [InlineData(Subtract, NumberSpec, LengthSpec)]
    [InlineData(Multiply, AngleSpec, AngleSpec)]
    [InlineData(Divide, NumberSpec, LengthSpec)]
    [InlineData(Power, LengthSpec, LengthSpec)]
    [InlineData(Power, LengthSpec, NumberSpec)]
    public void RefusesUnitsOutsideTheVerifiedCombinations(long operation, string left, string right) =>
        AssertRefused(new Binary(operation, new Num(4, left), new Num(operation == Power && right == NumberSpec ? 4 : 3, right)));

    [Theory]
    [InlineData(Sin, 0.5, AngleSpec, 0.479425538604203, UnitType.UT_Number)]
    [InlineData(Cos, 0.5, AngleSpec, 0.8775825618903728, UnitType.UT_Number)]
    [InlineData(Tan, 0.5, AngleSpec, 0.5463024898437905, UnitType.UT_Number)]
    [InlineData(Asin, 0.5, NumberSpec, 0.5235987755982989, UnitType.UT_Angle)]
    [InlineData(Acos, 0.5, NumberSpec, 1.0471975511965979, UnitType.UT_Angle)]
    [InlineData(Atan, 1, NumberSpec, 0.7853981633974483, UnitType.UT_Angle)]
    [InlineData(Exp, 2, NumberSpec, 7.38905609893065, UnitType.UT_Number)]
    [InlineData(Log, 100, NumberSpec, 2, UnitType.UT_Number)]
    [InlineData(Ln, 10, NumberSpec, 2.302585092994046, UnitType.UT_Number)]
    [InlineData(Sqrt, 9, NumberSpec, 3, UnitType.UT_Number)]
    [InlineData(Sqrt, 16, AreaSpec, 4, UnitType.UT_Length)]
    [InlineData(Sqrt, 0, NumberSpec, 0, UnitType.UT_Number)]
    [InlineData(Sqrt, 0, AreaSpec, 0, UnitType.UT_Length)]
    [InlineData(Asin, 1, NumberSpec, 1.5707963267948966, UnitType.UT_Angle)]
    [InlineData(Asin, -1, NumberSpec, -1.5707963267948966, UnitType.UT_Angle)]
    [InlineData(Acos, 1, NumberSpec, 0, UnitType.UT_Angle)]
    [InlineData(Acos, -1, NumberSpec, 3.141592653589793, UnitType.UT_Angle)]
    [InlineData(Abs, -3, LengthSpec, 3, UnitType.UT_Length)]
    [InlineData(Abs, -3, NumberSpec, 3, UnitType.UT_Number)]
    public void CalculatesTheMathFunctionsWithTheirUnits(long function, double argument, string spec, double expected, UnitType unit) =>
        AssertValue(expected, unit, Calculate(Call(function, new Num(argument, spec))));

    [Fact]
    public void PiIsTheValueRevitStores()
    {
        AssertValue(3.14159265358979, UnitType.UT_Number, Calculate(Call(Pi)));
        Assert.Equal(3.14159265358979, Calculate(Call(Pi)).Number);
    }

    // Revit's rule for a negative argument of round, roundup and rounddown has not been observed. These rows document the rule the
    // evaluator implements, half toward positive infinity, ceiling and floor, which docs/writing.md names as an assumption.
    [Theory]
    [InlineData(Round, 2.4, 2)]
    [InlineData(Round, 2.5, 3)]
    [InlineData(Round, 2.6, 3)]
    [InlineData(Round, -2.4, -2)]
    [InlineData(Round, -2.5, -2)]
    [InlineData(Round, -2.6, -3)]
    [InlineData(Round, 3, 3)]
    [InlineData(RoundUp, 2.2, 3)]
    [InlineData(RoundUp, -2.2, -2)]
    [InlineData(RoundUp, 3, 3)]
    [InlineData(RoundDown, 2.7, 2)]
    [InlineData(RoundDown, -2.2, -3)]
    [InlineData(RoundDown, 3, 3)]
    public void RoundsTheWayTheEvaluatorImplements(long function, double argument, double expected) =>
        AssertValue(expected, UnitType.UT_Number, Calculate(Call(function, new Num(argument))));

    [Theory]
    [InlineData(Round, 2.4999999999999)]
    [InlineData(RoundUp, 2.0000000000001)]
    [InlineData(RoundDown, 2.9999999999999)]
    public void RefusesRoundingThatRevitMaySuppressNearAStep(long function, double argument) =>
        AssertRefused(Call(function, new Num(argument)));

    [Fact]
    public void SelectsAndCombinesBooleans()
    {
        AssertValue(5, UnitType.UT_Number, Calculate(Call(If, True, new Num(5), new Num(7))));
        AssertValue(7, UnitType.UT_Number, Calculate(Call(If, False, new Num(5), new Num(7))));
        foreach (var (function, left, right, expected) in new (long, Node, Node, bool)[]
                 {
                     (Or, True, False, true), (Or, False, True, true), (Or, False, False, false),
                     (And, True, True, true), (And, True, False, false),
                 })
        {
            var value = Calculate(Call(function, left, right));
            Assert.Equal(ParameterFormulaEvaluator.ValueKind.Boolean, value.Kind);
            Assert.Equal(expected, value.Number != 0);
        }
        Assert.Equal(0, Calculate(Call(Not, True)).Number);
        Assert.Equal(1, Calculate(Call(Not, False)).Number);
    }

    [Fact]
    public void EvaluatesOnlyTheOperandsTheResultNeeds()
    {
        var unsupported = new Other();
        Assert.Equal(0, Calculate(Call(And, False, unsupported)).Number);
        Assert.Equal(1, Calculate(Call(Or, True, unsupported)).Number);
        AssertValue(5, UnitType.UT_Number, Calculate(Call(If, True, new Num(5), unsupported)));
        AssertValue(7, UnitType.UT_Number, Calculate(Call(If, False, unsupported, new Num(7))));
        AssertRefused(Call(And, True, unsupported));
        AssertRefused(Call(Or, False, unsupported));
    }

    [Theory]
    [InlineData(Sqrt, -1, NumberSpec)]
    [InlineData(Sqrt, -1, AreaSpec)]
    [InlineData(Log, 0, NumberSpec)]
    [InlineData(Log, 1e-10, NumberSpec)]
    [InlineData(Ln, 0, NumberSpec)]
    [InlineData(Ln, 1e-10, NumberSpec)]
    [InlineData(Asin, 2, NumberSpec)]
    [InlineData(Acos, -2, NumberSpec)]
    [InlineData(Tan, 1.5707963267948966, AngleSpec)]
    [InlineData(Exp, 100, NumberSpec)]
    public void RetainsTheStoredValueWhereRevitDoesNotProduceOne(long function, double argument, string spec) =>
        Assert.Null(Evaluate(Call(function, new Num(argument, spec))));

    [Fact]
    public void RetainsTheStoredValueForOverflowDivisionByZeroAndAnUndefinedOperand()
    {
        Assert.Null(Evaluate(new Binary(Power, new Num(2), new Num(1000))));
        Assert.Null(Evaluate(Call(Exp, new Num(1e308))));
        Assert.Null(Evaluate(new Binary(Power, new Num(10), new Num(-400))));
        Assert.Null(Evaluate(new Binary(Divide, new Num(1), new Num(0))));
        Assert.Null(Evaluate(new Binary(Divide, new Num(1), new Num(1e-10))));
        Assert.Null(Evaluate(new Binary(Add, Call(Sqrt, new Num(-1)), new Num(1))));
        Assert.Null(Evaluate(new Binary(Add, new Num(1), Call(Sqrt, new Num(-1)))));
        Assert.Null(Evaluate(Call(Sin, Call(Sqrt, new Num(-1)))));
        AssertValue(0, UnitType.UT_Number, Calculate(new Binary(Power, new Num(0), new Num(2))));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [InlineData(-8, 0.5)]
    public void RefusesAPowerWithoutADefinedResult(double number, double exponent) =>
        AssertRefused(new Binary(Power, new Num(number), new Num(exponent)));

    [Theory]
    [InlineData(1e-9, 0)]
    [InlineData(0, 1e-9)]
    [InlineData(1, 1 + 1e-12)]
    [InlineData(-1, -1 - 1e-12)]
    public void RefusesAChangeWithinTheComparisonTolerance(double before, double after) =>
        Assert.Equal("unverified-parameter-storage",
            Assert.Throws<RvtWriteRefusedException>(() => ParameterFormulaEvaluator.VerifyStableChange(before, after)).Kind);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(0, 2e-9)]
    [InlineData(-1, 1)]
    public void AcceptsAnIdenticalOrClearlyDifferentValue(double before, double after) =>
        ParameterFormulaEvaluator.VerifyStableChange(before, after);

    [Fact]
    public void RefusesWhatNeitherRevitNorTheEvaluatorDefines()
    {
        AssertRefused(Call(Sin, new Num(0.5)));
        AssertRefused(Call(Sqrt, new Num(4, LengthSpec)));
        AssertRefused(Call(Exp, new Num(1, LengthSpec)));
        AssertRefused(Call(15, new Num(1)));
        AssertRefused(Call(0, new Num(1)));
        AssertRefused(new Fn(Sin, [new Num(0.5, AngleSpec), new Num(0.5, AngleSpec)]));
        AssertRefused(new Fn(Pi, [new Num(1)]));
        AssertRefused(new Fn(Sin, [new Num(0.5, AngleSpec)], Count: 0));
        AssertRefused(Call(Sqrt, new Str("a")));
        AssertRefused(Call(If, new Num(1), new Num(5), new Num(7)));
        AssertRefused(Call(Not, new Num(1)));
        AssertRefused(new Binary(Add, new Str("a"), new Num(1)));
        AssertRefused(new Binary(Power, new Num(0), new Num(-1)));
        AssertRefused(new Num(double.NaN));
        AssertRefused(new Num(double.PositiveInfinity));
        AssertRefused(new Num(1, "autodesk.spec.aec:unknownSpec-1.0.0"));
        AssertRefused(new Other());
    }

    [Fact]
    public void RefusesAResultThatIsNotFinite()
    {
        var error = Assert.Throws<RvtWriteRefusedException>(() => Evaluate(new Binary(Multiply, new Num(1e200), new Num(1e200))));
        Assert.Equal("invalid-parameter-value", error.Kind);
        Assert.DoesNotContain("global", error.Message);
    }

    [Fact]
    public void ResolvesReferencesWithTheRemainingDepth()
    {
        var seen = new List<(long Id, int Remaining)>();
        Value Resolve(long id, int remaining)
        {
            seen.Add((id, remaining));
            return new(7, UnitType.UT_Length);
        }

        AssertValue(7, UnitType.UT_Length, Evaluate(new Ref(5), Resolve, depth: 10) ?? throw new InvalidOperationException());
        Assert.Equal([(5L, 9)], seen);
        seen.Clear();
        AssertValue(14, UnitType.UT_Length, Evaluate(new Paren(new Binary(Add, new Ref(6), new Ref(7))), Resolve, depth: 10)
            ?? throw new InvalidOperationException());
        Assert.Equal([(6L, 7), (7L, 7)], seen);
    }

    [Fact]
    public void RefusesAnExpressionDeeperThanTheVerifiedLimit()
    {
        Node deep = new Num(1);
        for (int i = 0; i < ParameterFormulaEvaluator.MaxDepth - 1; i++)
            deep = new Paren(deep);
        AssertValue(1, UnitType.UT_Number, Calculate(deep));
        var error = Assert.Throws<RvtWriteRefusedException>(() => Evaluate(new Paren(deep)));
        Assert.Equal("unverified-parameter-storage", error.Kind);
        Assert.Contains("depth", error.Message);
    }
}

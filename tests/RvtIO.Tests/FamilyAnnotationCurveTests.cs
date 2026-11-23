using System.Text;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The detail curve of a family (TfBlockDeserializer.ReadFamilyAnnotationCurve), read from blocks written for
/// the test: the root curve element, its curve driver and the line or the arc the driver owns. The values are small
/// numbers in feet, the way a family annotation is drawn.</summary>
public class FamilyAnnotationCurveTests
{
    private const ushort Line = 1736, Arc = 1974, Driver = 929, TypeDependentData = 1806, Sketch = 3606;

    [Fact]
    public void Line_IsTheSegmentBetweenItsTwoParameters()
    {
        var (line, arc, typeDependent, sketch) = Must(Block(Line,
            LinePayload(origin: [0.1, 0.2, 0], direction: [1, 0, 0], t0: 0.05, t1: 0.25, style: 77)));

        Assert.Null(arc);
        Assert.False(typeDependent);
        Assert.Equal(-1, sketch);
        Assert.NotNull(line);
        Close([0.15, 0.2, 0.35, 0.2], line.X0, line.Y0, line.X1, line.Y1);
        Assert.Equal(77, line.StyleId);
    }

    [Fact]
    public void Line_FollowsADirectionAndANegativeStartParameter()
    {
        var (line, _, _, _) = Must(Block(Line,
            LinePayload(origin: [0.3, 0.4, 0], direction: [0.6, 0.8, 0], t0: -0.1, t1: 0.2)));

        Assert.NotNull(line);
        Close([0.24, 0.32, 0.42, 0.56], line.X0, line.Y0, line.X1, line.Y1);
    }

    [Fact]
    public void Line_ReportsTheTypeDependentMarkAndTheSketchOfTheElement()
    {
        var (line, _, typeDependent, sketch) = Must(Block(Line, LinePayload(), typeDependent: true, sketch: 4321));

        Assert.NotNull(line);
        Assert.True(typeDependent);
        Assert.Equal(4321, sketch);
    }

    [Theory]
    [InlineData("point")]          // both parameters name the same point
    [InlineData("origin_z")]       // the line is off the family plane
    [InlineData("direction_z")]    // it leaves the plane
    [InlineData("flags")]          // bit 0x20 of the graphics info
    [InlineData("far_x")]          // a coordinate of a family annotation is below one foot
    [InlineData("far_y")]
    [InlineData("not_a_number")]
    public void Line_OutsideTheSupportedShapeIsRefused(string defect)
    {
        var payload = defect switch
        {
            "point" => LinePayload(t0: 0.1, t1: 0.1),
            "origin_z" => LinePayload(origin: [0, 0, 0.5]),
            "direction_z" => LinePayload(direction: [1, 0, 0.5]),
            "flags" => LinePayload(flags: 0x20),
            "far_x" => LinePayload(origin: [2, 0, 0]),
            "far_y" => LinePayload(origin: [0, -2, 0]),
            "not_a_number" => LinePayload(origin: [double.NaN, 0, 0]),
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };

        Assert.Null(Read(Block(Line, payload)));
    }

    [Fact]
    public void Line_KeepsOtherGraphicsFlagBits() => Assert.NotNull(Read(Block(Line, LinePayload(flags: 0x1F))));

    [Fact]
    public void Arc_IsTheSweepFromItsStartToItsEndAngle()
    {
        var (line, arc, _, _) = Must(Block(Arc,
            ArcPayload(center: [0.2, 0.3, 0], radius: 0.1, start: 0, end: Math.PI / 2, style: 12)));

        Assert.Null(line);
        Assert.NotNull(arc);
        Close([0.2, 0.3, 0.1, 0, 90], arc.X, arc.Y, arc.Radius, arc.Start, arc.End);
        Assert.Equal(12, arc.StyleId);
    }

    [Fact]
    public void Arc_OfAReflectedFamilySweepsTheOtherWay()
    {
        // The y axis points down, so a sweep that is counterclockwise in the arc's own frame is clockwise on the page.
        var (_, arc, _, _) = Must(Block(Arc, ArcPayload(x: [1, 0, 0], y: [0, -1, 0], start: 0, end: Math.PI / 2)));

        Assert.NotNull(arc);
        Close([0, -90], arc.Start, arc.End);
    }

    [Fact]
    public void Arc_OfARotatedFamilyAddsTheRotationOfItsAxes()
    {
        var (_, arc, _, _) = Must(Block(Arc, ArcPayload(x: [0, 1, 0], y: [-1, 0, 0], start: 0, end: Math.PI / 2)));

        Assert.NotNull(arc);
        Close([90, 180], arc.Start, arc.End);
    }

    [Fact]
    public void Arc_WithEqualEndAnglesIsTheFullCircle()
    {
        var (_, arc, _, _) = Must(Block(Arc, ArcPayload(start: 1, end: 1)));

        Assert.NotNull(arc);
        Assert.Equal(360, arc.End - arc.Start, 9);
    }

    [Fact]
    public void Arc_OfTwoPiIsTheFullCircle()
    {
        var (_, arc, _, _) = Must(Block(Arc, ArcPayload(start: 0, end: 2 * Math.PI)));

        Assert.NotNull(arc);
        Assert.Equal(360, arc.End - arc.Start, 9);
    }

    [Fact]
    public void Arc_ReportsTheTypeDependentMarkAndTheSketchOfTheElement()
    {
        var (_, arc, typeDependent, sketch) = Must(Block(Arc, ArcPayload(), typeDependent: true, sketch: 99));

        Assert.NotNull(arc);
        Assert.True(typeDependent);
        Assert.Equal(99, sketch);
    }

    [Theory]
    [InlineData("flags")]
    [InlineData("filled")]          // a filled circle is a region, not a curve
    [InlineData("radius_zero")]
    [InlineData("radius_foot")]     // the radius of a family annotation arc is below one foot
    [InlineData("radius_not_a_number")]
    [InlineData("center_far")]
    [InlineData("center_z")]
    [InlineData("axis_not_unit")]
    [InlineData("axes_not_square")]
    [InlineData("x_axis_z")]        // an axis that leaves the plane while its x and y parts stay unit and square
    [InlineData("y_axis_z")]
    [InlineData("sweep_reversed")]  // the end angle is before the start angle
    [InlineData("sweep_too_long")]
    [InlineData("angle_not_a_number")]
    public void Arc_OutsideTheSupportedShapeIsRefused(string defect)
    {
        var payload = defect switch
        {
            "flags" => ArcPayload(flags: 0x20),
            "filled" => ArcPayload(filled: true),
            "radius_zero" => ArcPayload(radius: 0),
            "radius_foot" => ArcPayload(radius: 1),
            "radius_not_a_number" => ArcPayload(radius: double.NaN),
            "center_far" => ArcPayload(center: [1, 0, 0]),
            "center_z" => ArcPayload(center: [0, 0, 0.1]),
            "axis_not_unit" => ArcPayload(x: [2, 0, 0]),
            "axes_not_square" => ArcPayload(y: [0.6, 0.8, 0]),
            "x_axis_z" => ArcPayload(x: [1, 0, 0.5]),
            "y_axis_z" => ArcPayload(y: [0, 1, 0.5]),
            "sweep_reversed" => ArcPayload(start: 1, end: 0.5),
            "sweep_too_long" => ArcPayload(start: 0, end: 7),
            "angle_not_a_number" => ArcPayload(start: double.NaN),
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };

        Assert.Null(Read(Block(Arc, payload)));
    }

    [Fact]
    public void ElementWithoutACurveDriver_IsRefused() => Assert.Null(Read(Block(Line, LinePayload(), driver: null)));

    [Fact]
    public void ElementWhoseDriverPointerIsNotACurveDriver_IsRefused()
        => Assert.Null(Read(Block(Line, LinePayload(), driver: Sketch)));

    [Fact]
    public void DriverOwningNeitherALineNorAnArc_IsRefused()
        => Assert.Null(Read(Block(Sketch, BitConverter.GetBytes(0))));

    [Theory]
    [InlineData("size")]
    [InlineData("footer")]
    [InlineData("class")]
    [InlineData("truncated")]
    public void DamagedFrame_IsRefused(string defect)
    {
        byte[] block = Block(Line, LinePayload());
        uint size = BitConverter.ToUInt32(block, 8);
        switch (defect)
        {
            case "size": BitConverter.GetBytes(size + 4).CopyTo(block, 8); break;
            case "footer": BitConverter.GetBytes(size + 4).CopyTo(block, block.Length - 4); break;
            case "class": BitConverter.GetBytes((ushort)839).CopyTo(block, 12); break;
            case "truncated": block = block[..^1]; break;
        }

        Assert.Null(TfBlockDeserializer.ReadFamilyAnnotationCurve(block, 0, BitConverter.ToUInt32(block, 8)));
    }

    [Fact]
    public void FrameOutsideTheBuffer_IsRefused()
    {
        byte[] block = Block(Line, LinePayload());
        uint size = BitConverter.ToUInt32(block, 8);

        Assert.Null(TfBlockDeserializer.ReadFamilyAnnotationCurve(block, 0, 5));
        Assert.Null(TfBlockDeserializer.ReadFamilyAnnotationCurve(block, -1, size));
        Assert.Null(TfBlockDeserializer.ReadFamilyAnnotationCurve(block, 1, size));
    }

    [Fact]
    public void BlockInsideALargerBuffer_IsReadFromItsStart()
    {
        byte[] block = Block(Line, LinePayload());
        byte[] buffer = [.. new byte[24], .. block, .. new byte[8]];

        var (line, _, _, _) = Must(TfBlockDeserializer.ReadFamilyAnnotationCurve(buffer, 24, BitConverter.ToUInt32(block, 8)));

        Assert.NotNull(line);
        Assert.Equal(0, line.X0, 9);
        Assert.Equal(0.1, line.X1, 9);
    }

    private static void Close(double[] expected, params double[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], actual[i], 9);
    }

    private static (TfBlockDeserializer.TitleBlockLine? Line, TfBlockDeserializer.FamilyAnnotationArc? Arc, bool TypeDependent, int SketchId)?
        Read(byte[] block) => TfBlockDeserializer.ReadFamilyAnnotationCurve(block, 0, BitConverter.ToUInt32(block, 8));

    private static (TfBlockDeserializer.TitleBlockLine? Line, TfBlockDeserializer.FamilyAnnotationArc? Arc, bool TypeDependent, int SketchId)
        Must(byte[] block) => Must(Read(block));

    private static (TfBlockDeserializer.TitleBlockLine? Line, TfBlockDeserializer.FamilyAnnotationArc? Arc, bool TypeDependent, int SketchId)
        Must((TfBlockDeserializer.TitleBlockLine? Line, TfBlockDeserializer.FamilyAnnotationArc? Arc, bool TypeDependent, int SketchId)? result)
        => result ?? throw new Xunit.Sdk.XunitException("The curve was refused.");

    /// <summary>A GLine after its graphics info: the info (tag, control, style, flags), the end parameters, the origin and
    /// the direction.</summary>
    private static byte[] LinePayload(double[]? origin = null, double[]? direction = null, double t0 = 0, double t1 = 0.1,
        int style = 5, uint flags = 0)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);
        writer.Write(0); writer.Write(0); writer.Write(style); writer.Write(flags);
        writer.Write(t0); writer.Write(t1);
        foreach (double value in origin ?? [0, 0, 0]) writer.Write(value);
        foreach (double value in direction ?? [1, 0, 0]) writer.Write(value);
        return stream.ToArray();
    }

    /// <summary>A GArc: the graphics info, the end angles, the two axes, the radius, the center and the fill flag.</summary>
    private static byte[] ArcPayload(double[]? x = null, double[]? y = null, double[]? center = null, double radius = 0.05,
        double start = 0, double end = Math.PI, int style = 5, uint flags = 0, bool filled = false)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);
        writer.Write(0); writer.Write(0); writer.Write(style); writer.Write(flags);
        writer.Write(start); writer.Write(end);
        foreach (double value in x ?? [1, 0, 0]) writer.Write(value);
        foreach (double value in y ?? [0, 1, 0]) writer.Write(value);
        writer.Write(radius);
        foreach (double value in center ?? [0, 0, 0]) writer.Write(value);
        writer.Write(filled ? (byte)1 : (byte)0);
        return stream.ToArray();
    }

    /// <summary>The curve element: the root fields, then the objects its pointers queue in order (the type-dependent data,
    /// the sketch cell, the curve driver) and the curve the driver points to.</summary>
    private static byte[] Block(ushort curveClass, byte[] curve, bool typeDependent = false, int? sketch = null,
        ushort? driver = Driver)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);
        writer.Write(200u); writer.Write(0u); writer.Write(0u); writer.Write((ushort)838);
        foreach (var field in TfBlockDeserializer.SerializationFields(838)!)
        {
            if (field.Name == "m_pGeomTable" && typeDependent) Pointer(writer, TypeDependentData);
            else if (field.Name == "m_cellList" && sketch != null) Pointer(writer, Sketch);
            else if (field.Name == "m_pCurveDriver" && driver != null) Pointer(writer, driver.Value);
            else if (field.Name == "m_referenceType") { writer.Write(0); writer.Write(0); }   // an int and a hidden int
            else Default(writer, field);
        }
        if (typeDependent) writer.Write(0u);              // the data of the type-dependent object: no parameters
        if (sketch != null) writer.Write(sketch.Value);   // the group of the sketch cell
        if (driver == Driver)
            foreach (var field in TfBlockDeserializer.SerializationFields(Driver)!)
            {
                if (field.Name == "m_pCrv") Pointer(writer, curveClass);
                else Default(writer, field);
            }
        else if (driver != null) writer.Write(0);
        writer.Write(curve);
        byte[] block = stream.ToArray();
        uint size = (uint)block.Length - 12;
        BitConverter.GetBytes(size).CopyTo(block, 8);
        return [.. block, .. BitConverter.GetBytes(size)];
    }

    private static void Pointer(BinaryWriter writer, ushort cls)
    {
        writer.Write(uint.MaxValue);
        writer.Write(cls);
    }

    /// <summary>A field with a zero value: a null pointer, an empty list, a zero number, an element id of -1.</summary>
    private static void Default(BinaryWriter writer, TfBlockDeserializer.TfField field)
    {
        if ((field.Mods >> 4) == 5) { writer.Write(0u); return; }
        switch (field.Type)
        {
            case 1: writer.Write((byte)0); break;
            case 4: writer.Write(0); break;
            case 7: writer.Write(0d); break;
            case 14: writer.Write(field.Ref == 20 ? -1 : 0); break;
            default: throw new InvalidOperationException(field.Name);
        }
    }
}

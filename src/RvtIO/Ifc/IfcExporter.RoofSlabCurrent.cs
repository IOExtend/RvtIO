using RvtIO.Geometry;
using System.Text;
using RvtIO.Model;

namespace RvtIO.Ifc;

public partial class IfcExporter
{
    /// <summary>The GlobalId of the slab of a roof is the sub-element 2 of the ExportId of the roof.</summary>
    internal const int RoofSlabSubElementIndex = 2;

    /// <summary>Roof export of the current profile: when the body of a roof is a vertical extrusion
    /// (its scaled length is not zero) the IfcRoof carries no representation and aggregates
    /// an IfcSlab (ROOF) that holds the body. The test is the one applied to a single solid of a wall
    /// (<see cref="WallExtrusion.TryFindVerticalExtrusion"/>).</summary>
    internal static bool RoofIsExportedWithSlabCurrent(Element roof)
    {
        if (roof.CategoryId != RoofCategoryId || roof.Geometry?.Faces is not { Count: > 0 } faces) return false;
        var body = roof.Geometry.BrepSolids is { Count: > 0 } solids
            ? solids[0].FaceIndices.Where(index => (uint)index < (uint)faces.Count).Select(index => faces[index]).ToList()
            : faces;
        return body.Count > 0 && WallExtrusion.TryFindVerticalExtrusion(body, out _);
    }

    /// <summary>The Name of the slab of a roof: the Name of the roof with ":1" appended. The Name is read back as an ANSI
    /// string, so the UTF-8 bytes of a non-ASCII name come out as Windows-1251 characters (a Cyrillic name turns into mojibake).</summary>
    internal static string RoofSlabNameCurrent(string roofName)
    {
        var text = new StringBuilder();
        foreach (byte value in Encoding.UTF8.GetBytes(roofName))
            text.Append(value < 0x80 ? (char)value : Windows1251High[value - 0x80]);
        return text.Append(":1").ToString();
    }

    /// <summary>Windows-1251 code points of the bytes 0x80..0xFF (0x98 is unassigned and maps to U+0098).</summary>
    private static readonly char[] Windows1251High = BuildWindows1251High();

    private static char[] BuildWindows1251High()
    {
        char[] table = new char[128];
        int[] upperHalf =
        [
            0x0402, 0x0403, 0x201A, 0x0453, 0x201E, 0x2026, 0x2020, 0x2021, 0x20AC, 0x2030, 0x0409, 0x2039, 0x040A, 0x040C, 0x040B, 0x040F,
            0x0452, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2013, 0x2014, 0x0098, 0x2122, 0x0459, 0x203A, 0x045A, 0x045C, 0x045B, 0x045F,
            0x00A0, 0x040E, 0x045E, 0x0408, 0x00A4, 0x0490, 0x00A6, 0x00A7, 0x0401, 0x00A9, 0x0404, 0x00AB, 0x00AC, 0x00AD, 0x00AE, 0x0407,
            0x00B0, 0x00B1, 0x0406, 0x0456, 0x0491, 0x00B5, 0x00B6, 0x00B7, 0x0451, 0x2116, 0x0454, 0x00BB, 0x0458, 0x0405, 0x0455, 0x0457,
        ];
        for (int i = 0; i < upperHalf.Length; i++) table[i] = (char)upperHalf[i];
        for (int i = 0; i < 64; i++) table[64 + i] = (char)(0x0410 + i);
        return table;
    }

    /// <summary>Writes the IfcSlab (ROOF) of a roof that <see cref="RoofIsExportedWithSlabCurrent"/> selects: the body of the
    /// roof under a copy of its placement, GlobalId = sub-element 2 of the ExportId, aggregated by the roof. Returns the
    /// entity id of the slab.</summary>
    private int WriteRoofSlabCurrent(StepWriter w, int ownerHistId, Element roof, int roofEntityId, string roofName,
        string objectType, int parentPlacementId, int axisId, int shapeId)
    {
        int placement = w.Add("IFCLOCALPLACEMENT", $"{R(parentPlacementId)},{R(axisId)}");
        string slabGlobalId = ResolveExactElementGlobalId(roof, _usedExportIds, RoofSlabSubElementIndex)
            ?? IfcGuid.FromName($"roof-slab:{roof.Id}");
        int slab = w.Add("IFCSLAB",
            $"{S(slabGlobalId)},{R(ownerHistId)},{S(RoofSlabNameCurrent(roofName))},$,{S(objectType)},{R(placement)},{R(shapeId)},"
            + $"{S(roof.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))},.ROOF.");
        // The relation gets a random GlobalId, so no value can be matched.
        w.Add("IFCRELAGGREGATES",
            $"{S(IfcGuid.FromName($"roof-slab-aggregate:{roof.Id}"))},{R(ownerHistId)},$,$,{R(roofEntityId)},{StepWriter.List(R(slab))}");
        return slab;
    }
}

using System.Linq;
using RvtIO.BuiltIns;
using RvtIO.Model;

namespace RvtIO.Cli;

/// <summary>
/// Diagnostic-only, gated behind <c>--list-category &lt;categoryId&gt; &lt;out.csv&gt;</c> (see Program.cs). Never called from the
/// standard export path.
/// <para>
/// <c>--inspect-batch</c> (InspectBatchDump.cs) needs an existing list of element ids. This dump produces one by scanning the parsed
/// <c>RevitFileInfo.Elements</c> (one <c>RvtFileReader.Read</c> pass) for every element whose <c>CategoryId</c> matches, and writes its
/// Id plus a few identity and parameter fields (Name, a lenient "Number" parameter lookup, level name via LEVEL_PARAM, geometry
/// presence counts, bbox). It is deliberately narrow, not a general parameter dump.
/// </para>
/// <para>Never write a .json output path here: a .json output path anywhere in this CLI triggers the full JSON export.</para>
/// </summary>
static class ListCategoryDump
{
    public static void Run(RevitFileInfo info, int categoryId, string outPath)
    {
        var byId = new Dictionary<int, Element>(info.Elements.Count);
        foreach (var e in info.Elements) byId.TryAdd(e.Id, e);

        using var w = new StreamWriter(outPath, false, System.Text.Encoding.UTF8);
        w.WriteLine(string.Join(",",
            "Id", "Name", "FamilyName", "NumberParam", "RoomNumberBip",
            "LevelParamId", "RoomLevelId", "LevelId", "LevelName",
            "FacesCount", "SolidsCount",
            "BBoxMinX", "BBoxMinY", "BBoxMinZ", "BBoxMaxX", "BBoxMaxY", "BBoxMaxZ",
            "SketchLoopsCount", "SketchLoopsTotalPoints",
            "References"));

        int found = 0;
        foreach (var e in info.Elements)
        {
            if (e.CategoryId != categoryId) continue;
            found++;

            string? numberParam = NamedStringParam(e, "Number");
            // ROOM_NUMBER (BuiltInParameter -1006901): direct lookup, in case Definition.Name is not literally "Number".
            string? roomNumberBip = StringParam(e, BuiltInParameter.ROOM_NUMBER);
            int? levelParamId = IntParam(e, BuiltInParameter.LEVEL_PARAM);
            // ApplyRoomParams (ElementBuilder.cs) synthesizes ROOM_LEVEL_ID from the room's own References when a referenced element
            // resolves to OST_Levels: a second level source besides the generic LEVEL_PARAM.
            int? roomLevelId = IntParam(e, BuiltInParameter.ROOM_LEVEL_ID);
            int? levelId = levelParamId ?? roomLevelId;
            string? levelName = levelId is { } lid && byId.TryGetValue(lid, out var levelElem)
                ? levelElem.Name : null;

            int faces = e.Geometry?.Faces?.Count ?? 0;
            int solids = e.Geometry?.BrepSolids?.Count ?? 0;
            var bbox = e.BoundingBox;
            int sketchLoopsCount = e.SketchLoops?.Count ?? 0;
            int sketchLoopsTotalPoints = e.SketchLoops?.Sum(loop => loop.Count) ?? 0;

            w.WriteLine(string.Join(",",
                e.Id, Csv(e.Name), Csv(e.FamilyName), Csv(numberParam), Csv(roomNumberBip),
                Cell(levelParamId), Cell(roomLevelId), Cell(levelId), Csv(levelName),
                faces, solids,
                Cell(bbox is { Length: >= 6 } ? bbox[0] : null),
                Cell(bbox is { Length: >= 6 } ? bbox[1] : null),
                Cell(bbox is { Length: >= 6 } ? bbox[2] : null),
                Cell(bbox is { Length: >= 6 } ? bbox[3] : null),
                Cell(bbox is { Length: >= 6 } ? bbox[4] : null),
                Cell(bbox is { Length: >= 6 } ? bbox[5] : null),
                sketchLoopsCount, sketchLoopsTotalPoints,
                Csv(e.References is { Count: > 0 } refs ? string.Join(";", refs) : null)));
        }
        Console.Error.WriteLine($"[list-category] category={categoryId}: {found} elements -> {outPath}");
    }

    private static int? IntParam(Element e, BuiltInParameter bip) =>
        e.LookupParameter(bip)?.Value is int v ? v : null;

    private static string? StringParam(Element e, BuiltInParameter bip) =>
        e.LookupParameter(bip)?.Value is string v ? v : null;

    private static string? NamedStringParam(Element e, string name)
    {
        foreach (var p in e.Parameters)
            if (p.DefinitionName is string n
                && string.Equals(n.Replace(" ", null), name.Replace(" ", null),
                    StringComparison.InvariantCultureIgnoreCase)
                && p.Value is string v)
                return v;
        return null;
    }

    private static string Cell(int? v) =>
        v.HasValue ? v.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

    private static string Cell(double? v) =>
        v.HasValue ? v.Value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture) : "";

    private static string Csv(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        bool needsQuote = s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r');
        if (!needsQuote) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}

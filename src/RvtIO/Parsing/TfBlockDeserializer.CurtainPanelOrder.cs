namespace RvtIO.Parsing;

internal static partial class TfBlockDeserializer
{
    /// <summary>The panel ids of the curtain grids of a wall's own root (VWall subclass), in stored order: the grids of
    /// the wall's CGDrivers one after the other, each grid's <c>CurtainGrid.m_panelIds</c> as written (the IFC exporter
    /// appends the panel ids of each driver's grid in this order). Null when the wall does not decode completely, has no curtain
    /// grid, or a panel list is not exactly a count followed by that many ids. Zero ids (empty cells) are kept.</summary>
    internal static int[]? ExtractCurtainGridPanelIds(byte[] data, int start, uint size, ushort classId,
        WallCompleteInputRead? complete = null)
    {
        if (!IsSubclassOf(classId, 362)) return null;
        var input = complete == null
            ? ExtractOwnWallCompleteInputs(data, start, size, classId, out _)
            : complete.Read(out _);
        if (input == null) return null;
        List<int>? ids = null;
        foreach (var grid in input.LaterObjects)
        {
            if (grid.ClassId != 901) continue;
            var lists = grid.Fields.Where(field => field is { Declaring: 901, Name: "m_panelIds" }).ToList();
            if (lists.Count != 1) return null;
            byte[] bytes = lists[0].Bytes;
            if (bytes.Length < 4) return null;
            uint count = BitConverter.ToUInt32(bytes, 0);
            if (count > 1_000_000 || bytes.Length != 4 + 4L * count) return null;
            ids ??= [];
            for (int index = 0; index < count; index++) ids.Add(BitConverter.ToInt32(bytes, 4 + 4 * index));
        }
        return ids?.ToArray();
    }
}

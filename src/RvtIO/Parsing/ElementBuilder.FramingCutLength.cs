using RvtIO.Model;

namespace RvtIO.Parsing;

internal static partial class ElementBuilder
{
    /// <summary>
    /// yz Justification (StructuralFramingJustification m_yzJustification): 0 Uniform, 1
    /// Independent.
    /// </summary>
    private const int IndependentJustificationParamId = -1152361;

    /// <summary>
    /// Candidate for a straight structural framing member whose ends are not coped: the extent of
    /// its solids along the drive line (CurveDriver.m_pCrv GLine direction).
    /// </summary>
    internal static void ApplyFramingCutLength(RevitFileInfo info)
    {
        foreach (var e in info.Elements)
        {
            if (e.CategoryId != -2001320 || e.NativeMasterSymbolId == null
                || e.CurveDriverLineDirection is not { } direction || e.FramingCopingDistance != -1
                || e.Geometry?.ReadFaces is not { Count: > 0 } faces
                || e.Parameters.FirstOrDefault(p => p.Id == IndependentJustificationParamId)?.Value is not (null or 0))
                continue;
            double min = double.MaxValue, max = double.MinValue;
            foreach (var face in faces)
                foreach (var vertex in face.OuterVertices ?? [])
                {
                    double along = vertex[0] * direction.X + vertex[1] * direction.Y + vertex[2] * direction.Z;
                    if (along < min) min = along;
                    if (along > max) max = along;
                }
            if (max > min) SetParam(e, -1001384, "STRUCTURAL_FRAME_CUT_LENGTH", StorageType.Double, max - min);
        }
    }

    internal static void ApplyFramingCopingDistance(RevitFileInfo info)
    {
        foreach (var e in info.Elements)
            if (e.CategoryId == -2001320 && e.FramingCopingDistance is >= 0 and double distance)
                SetParam(e, -1001559, "STRUCTURAL_FRAME_COPING_DISTANCE", StorageType.Double, distance);
    }

    /// <summary>
    /// Attachment of a column end (AttachInfo 699, m_baseOrTop 1 = top, 0 = base): the offset, the
    /// justification and the "attached" flag of that end, as Offset From Attachment At Top/Base,
    /// Attachment Justification At Top/Base and Top/Base is Attached.
    /// </summary>
    internal static void ApplyColumnAttachments(RevitFileInfo info)
    {
        foreach (var e in info.Elements)
        {
            if (e.CategoryId != -2001330 || e.ColumnAttachment is not { } attachment) continue;
            var (offset, justification, attached, otherOffset, otherJustification) = attachment.IsTop
                ? (-1002557, -1002555, -1002559, -1002558, -1002556)
                : (-1002558, -1002556, -1002560, -1002557, -1002555);
            e.Parameters.RemoveAll(p => p.Id == otherOffset || p.Id == otherJustification);
            SetParam(e, offset, attachment.IsTop ? "COLUMN_TOP_ATTACHMENT_OFFSET_PARAM" : "COLUMN_BASE_ATTACHMENT_OFFSET_PARAM", StorageType.Double, attachment.Offset);
            SetParam(e, justification, attachment.IsTop ? "COLUMN_TOP_ATTACH_JUSTIFICATION_PARAM" : "COLUMN_BASE_ATTACH_JUSTIFICATION_PARAM", StorageType.Integer, attachment.Justification);
            SetParam(e, attached, attachment.IsTop ? "COLUMN_TOP_ATTACHED_PARAM" : "COLUMN_BASE_ATTACHED_PARAM", StorageType.Integer, 1);
        }
    }

    /// <summary>
    /// Moves With Grids (INSTANCE_MOVES_WITH_GRID_PARAM) of the instances that have it (structural
    /// and architectural columns, structural foundations): FamilyInstance.m_useOffsetPos.
    /// </summary>
    internal static void ApplyMovesWithGrids(RevitFileInfo info)
    {
        foreach (var e in info.Elements)
            if (e.CategoryId is -2001330 or -2000100 or -2001300 && e.NativeMasterSymbolId != null && e.InstanceUsesOffsetPosition is bool keeps)
                SetParam(e, -1001371, "INSTANCE_MOVES_WITH_GRID_PARAM", StorageType.Integer, keeps ? 1 : 0);
    }
}

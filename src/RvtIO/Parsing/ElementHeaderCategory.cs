using RvtIO.Internal;

namespace RvtIO.Parsing;

/// <summary>
/// The ElementHeader every element record starts with (schema class 1340): its m_categroryId,
/// m_designOptionId and the axis-aligned outline it stores.
/// </summary>
internal static class ElementHeaderCategory
{
    private const int RegenCountOffset = 10;
    private const int RegenEntryLength = 14;

    // The fields of the header behind m_regenHistory, in the order of the schema: the six ids
    // m_categroryId, m_familyId, m_ownerViewId, m_designOptionId, m_unplacedOwnerId and m_miscId, then
    // m_viewRules (four bytes of flags, all ones where no rule is set), m_abFlags4Bytes (u32), m_classDef (a
    // class reference of two bytes) and the two owned pointers m_pBBox and m_parents. A pointer is a u32
    // handle and, when the handle is not 0, a u16 class tag; the objects the pointers name follow the fields
    // in the order the pointers were met, so the Outline is the first of them: six doubles.
    private const int IdFieldsBeforeViewRules = 6;
    private const int ViewRulesLength = 4, FlagsLength = 4, ClassDefinitionLength = 2;
    private const uint NewObjectHandle = 0xFFFFFFFF;
    private const int OutlineLength = 6 * sizeof(double);

    // The records the anchored walk reads are in the Revit 2022 layout (the R22 layout turns the later
    // releases into it), whose class tags are those of the embedded format schema.
    private static readonly Lazy<ushort> OutlineTag = new(() => (ushort)TagOfShippedSchema("Outline"));
    private static readonly Lazy<ushort> ParentsTag = new(() => (ushort)TagOfShippedSchema("ElementParents"));
    private static readonly ElementHeaderBox AbsentOutline = new(false, null);

    private static int TagOfShippedSchema(string name)
    {
        foreach (var (id, tfClass) in TfBlockDeserializer.Schemas)
            if (tfClass.Name == name) return id;
        throw new InvalidOperationException($"The embedded format schema has no class {name}.");
    }

    /// <summary>
    /// Reads m_categroryId of the record that starts at <paramref name="recordStart"/>; null when
    /// the bytes do not form a complete ElementHeader record (class tag, declared size or entry
    /// count do not fit the buffer).
    /// </summary>
    public static int? Read(byte[] buffer, int recordStart)
    {
        if (recordStart < 0 || (long)recordStart + RegenCountOffset + 4 > buffer.Length) return null;
        ushort recordClass = BitConverter.ToUInt16(buffer, recordStart + 8);
        if (recordClass is not (0x053C or 0x04F8)) return null;
        long recordEnd = (long)recordStart + 12 + BitConverter.ToUInt32(buffer, recordStart + 4);
        long categoryOffset = (long)recordStart + RegenCountOffset + 4
            + RegenEntryLength * (long)BitConverter.ToUInt32(buffer, recordStart + RegenCountOffset);
        if (recordEnd > buffer.Length || categoryOffset + 4 > recordEnd) return null;
        return BitConverter.ToInt32(buffer, (int)categoryOffset);
    }

    /// <summary>
    /// Reads ElementHeader.m_designOptionId of the same record: the fields m_familyId and
    /// m_ownerViewId (int32 each) lie between the category and the design option.
    /// </summary>
    public static int? ReadDesignOption(byte[] buffer, int recordStart)
    {
        if (Read(buffer, recordStart) is null) return null;
        long recordEnd = (long)recordStart + 12 + BitConverter.ToUInt32(buffer, recordStart + 4);
        long optionOffset = (long)recordStart + RegenCountOffset + 4
            + RegenEntryLength * (long)BitConverter.ToUInt32(buffer, recordStart + RegenCountOffset) + 12;
        return optionOffset + 4 <= recordEnd ? BitConverter.ToInt32(buffer, (int)optionOffset) : null;
    }

    /// <summary>
    /// The axis-aligned outline the record that starts at <paramref name="recordStart"/> stores (see
    /// <see cref="ReadOutline"/>); null when the bytes do not form a complete ElementHeader record.
    /// </summary>
    public static ElementHeaderBox? ReadBoundingBox(byte[] buffer, int recordStart)
    {
        if (Read(buffer, recordStart) is null) return null;
        long recordEnd = (long)recordStart + 12 + BitConverter.ToUInt32(buffer, recordStart + 4);
        long categoryOffset = (long)recordStart + RegenCountOffset + 4
            + RegenEntryLength * (long)BitConverter.ToUInt32(buffer, recordStart + RegenCountOffset);
        return ReadOutline(buffer, categoryOffset, recordEnd, 4, OutlineTag.Value, ParentsTag.Value);
    }

    /// <summary>
    /// The outline of a header whose m_categroryId lies at <paramref name="categoryAt"/> and whose record ends at
    /// <paramref name="recordEnd"/>: the position of m_pBBox follows from the widths of the fields in front of it
    /// (the ids are <paramref name="idWidth"/> bytes wide), so nothing is searched for. The pointer is 0 when the
    /// header stores no outline, or a new object of class <paramref name="outlineTag"/> whose six little-endian
    /// doubles (min xyz, max xyz, feet, project coordinates) follow the pointers; m_parents behind it is a new
    /// object of class <paramref name="parentsTag"/> in every header, and the structure counts as read only
    /// where it is. Null when the bytes do not have the structure, or when the six doubles do not form a box
    /// (a counter must not be taken for one, so only finite ordered values count).
    /// </summary>
    internal static ElementHeaderBox? ReadOutline(byte[] buffer, long categoryAt, long recordEnd, int idWidth, int outlineTag, int parentsTag)
    {
        if (recordEnd > buffer.Length) return null;
        long at = categoryAt + (long)IdFieldsBeforeViewRules * idWidth;
        if (at + ViewRulesLength + FlagsLength + ClassDefinitionLength + 4 > recordEnd) return null;
        at += ViewRulesLength + FlagsLength + ClassDefinitionLength;

        uint boxHandle = BitConverter.ToUInt32(buffer, (int)at);
        at += 4;
        bool present = boxHandle != 0;
        if (present)
        {
            if (boxHandle != NewObjectHandle || at + 2 > recordEnd || BitConverter.ToUInt16(buffer, (int)at) != outlineTag) return null;
            at += 2;
        }
        if (at + 6 > recordEnd || BitConverter.ToUInt32(buffer, (int)at) != NewObjectHandle
            || BitConverter.ToUInt16(buffer, (int)at + 4) != parentsTag)
            return null;
        at += 6;

        if (!present) return AbsentOutline;
        if (at + OutlineLength > recordEnd) return null;
        var box = new double[6];
        for (int d = 0; d < 6; d++) box[d] = BitConverter.ToDouble(buffer, (int)at + sizeof(double) * d);
        if (box.Any(value => !double.IsFinite(value)) || box[0] > box[3] || box[1] > box[4] || box[2] > box[5]) return null;
        return new ElementHeaderBox(true, box);
    }

    /// <summary>
    /// Stores the header category, design option and outline of a record the anchored stream walk
    /// accepted. An id whose records disagree (or whose header cannot be read) is stored as null,
    /// which stays null: unknown is never turned into a guess.
    /// </summary>
    public static void Record(ParsingState state, byte[] buffer, int recordStart, uint elementId)
    {
        int? category = Read(buffer, recordStart);
        if (!state.HeaderCategories.TryGetValue(elementId, out int? previous)) state.HeaderCategories[elementId] = category;
        else if (previous != category) state.HeaderCategories[elementId] = null;
        int? option = ReadDesignOption(buffer, recordStart);
        if (!state.HeaderDesignOptions.TryGetValue(elementId, out int? previousOption)) state.HeaderDesignOptions[elementId] = option;
        else if (previousOption != option) state.HeaderDesignOptions[elementId] = null;
        Merge(state.HeaderBoundingBoxes, elementId, ReadBoundingBox(buffer, recordStart));
    }

    /// <summary>
    /// Adds the outline verdict of one more record of an element: the first verdict stands, a verdict that
    /// differs from it (or an unknown one) turns the element unknown, and unknown stays unknown.
    /// </summary>
    internal static void Merge<TId>(Dictionary<TId, ElementHeaderBox?> boxes, TId elementId, ElementHeaderBox? box)
        where TId : notnull
    {
        if (!boxes.TryGetValue(elementId, out ElementHeaderBox? previousBox)) boxes[elementId] = box;
        else if (!SameBox(previousBox, box)) boxes[elementId] = null;
    }

    private static bool SameBox(ElementHeaderBox? first, ElementHeaderBox? second)
    {
        if (ReferenceEquals(first, second)) return true;
        if (first is null || second is null) return false;
        return first.Present == second.Present
            && (first.Box == second.Box || first.Box != null && second.Box != null
                && first.Box.SequenceEqual(second.Box));
    }
}

/// <summary>The outline verdict of an element's header records: a stored box, or the absence of one.</summary>
internal sealed record ElementHeaderBox(bool Present, double[]? Box);

using System.Text;

namespace RvtIO.Parsing;

/// <summary>
/// Parses Formats/Latest class schema table to extract class names and field definitions.
/// Each class has: name (ASCII), parent reference, and field list (name + type).
/// </summary>
internal static class TfClassRegistry
{
    /// <summary>A field in a Tf class schema.</summary>
    internal record TfField(string Name, byte Type);

    /// <summary>A class in the Tf schema registry.</summary>
    internal record TfClassDef(string Name, List<TfField> Fields);

    /// <summary>
    /// Parse Formats/Latest decompressed data and return the class name to fields mapping.
    /// Uses field name anchors (m_xxx pattern) to reliably extract field definitions
    /// even when exact binary layout varies.
    /// </summary>
    public static Dictionary<string, TfClassDef> Parse(byte[] data)
    {
        var classAtOffset = new List<(int offset, string name)>();
        for (int i = 0; i + 2 < data.Length; i++)
        {
            ushort nl = BitConverter.ToUInt16(data, i);
            if (nl < 2 || nl > 100 || i + 2 + nl > data.Length) continue;
            bool ascii = true;
            for (int k = 0; k < nl && ascii; k++)
                if (data[i + 2 + k] < 0x21 || data[i + 2 + k] > 0x7E) ascii = false;
            if (!ascii) continue;
            string name = Encoding.ASCII.GetString(data, i + 2, nl);
            if (!name.All(c => char.IsLetterOrDigit(c) || c == '_')) continue;
            if (!char.IsUpper(name[0]) && !name.StartsWith("std")) continue;
            classAtOffset.Add((i, name));
            i += 1 + nl;
        }

        var fieldAtOffset = new List<(int offset, string name, byte type)>();
        for (int i = 0; i + 8 < data.Length; i++)
        {
            uint fl = BitConverter.ToUInt32(data, i);
            if (fl < 3 || fl > 60 || i + 4 + (int)fl + 1 > data.Length) continue;
            bool ascii = true;
            for (int k = 0; k < (int)fl && ascii; k++)
                if (data[i + 4 + k] < 0x20 || data[i + 4 + k] > 0x7E) ascii = false;
            if (!ascii) continue;
            string fname = Encoding.ASCII.GetString(data, i + 4, (int)fl);
            if (!fname.StartsWith("m_")) continue;
            byte ftype = data[i + 4 + (int)fl];
            fieldAtOffset.Add((i, fname, ftype));
            i += 3 + (int)fl;
        }

        var result = new Dictionary<string, TfClassDef>();
        int classIdx = 0;
        foreach (var (fOff, fName, fType) in fieldAtOffset)
        {
            while (classIdx + 1 < classAtOffset.Count && classAtOffset[classIdx + 1].offset < fOff)
                classIdx++;
            string className = classAtOffset[classIdx].name;
            if (!result.ContainsKey(className))
                result[className] = new TfClassDef(className, new List<TfField>());
            result[className].Fields.Add(new TfField(fName, fType));
        }

        return result;
    }

    /// <summary>
    /// Return the class names in the exact order they appear in Formats/Latest.
    /// </summary>
    public static List<string> ParseOrderedClassNames(byte[] data)
    {
        var names = new List<string>();
        for (int i = 0; i + 2 < data.Length; i++)
        {
            ushort nl = BitConverter.ToUInt16(data, i);
            if (nl < 2 || nl > 100 || i + 2 + nl > data.Length) continue;
            bool ascii = true;
            for (int k = 0; k < nl && ascii; k++)
                if (data[i + 2 + k] < 0x21 || data[i + 2 + k] > 0x7E) ascii = false;
            if (!ascii) continue;
            string name = Encoding.ASCII.GetString(data, i + 2, nl);
            if (!name.All(c => char.IsLetterOrDigit(c) || c == '_')) continue;
            if (!char.IsUpper(name[0]) && !name.StartsWith("std")) continue;
            names.Add(name);
            i += 1 + nl;
        }
        return names;
    }

    /// <summary>Get human-readable type name for a field type byte.</summary>
    public static string TypeName(byte type) => type switch
    {
        0x01 => "bool", 0x02 => "i16[]", 0x03 => "u8", 0x04 => "i32",
        0x06 => "f32", 0x07 => "f64", 0x08 => "string", 0x0E => "ref",
        _ => $"0x{type:X2}"
    };

    /// <summary>
    /// R22 Tf classIdx to BuiltInCategory mapping.
    /// Maps the Tf class index (low 16 bits of block classIdx) to a Revit category.
    /// </summary>
    public static readonly Dictionary<ushort, int> R22TfClassIdToCategory = new()
    {
        // Walls
        [0x0DC4] = -2000011, // OST_Walls (primary VWall classIdx)
        [0x0200] = -2000011, // OST_Walls (alternate)
        [0x1026] = -2000011, // OST_Walls (alternate)
        [0x0392] = -2000011, // OST_Walls
        [0x0169] = -2000011, // OST_Walls
        [0x09FF] = -2000011, // OST_Walls
        [0x0E52] = -2000065, // OST_StackedWalls

        // Floors / Ceilings
        [0x077B] = -2000032, // OST_Floors
        [0x0779] = -2000032, // OST_Floors
        [0x0286] = -2000038, // OST_Ceilings

        // Levels
        [0x0744] = -2000240, // OST_Levels
        [0x08FB] = -2000240, // OST_Levels
        [0x08FD] = -2000240, // OST_Levels
        [0x1100] = -2000240, // OST_Levels
        [0x02DE] = -2000240, // OST_Levels
        [0x01BE] = -2000240, // OST_Levels
        [0x01BD] = -2000240, // OST_Levels
        [0x06F9] = -2000240, // OST_Levels
        [0x0902] = -2000240, // OST_Levels

        // Dimensions / constraints: 0x091F blocks decode to dimension reference-point geometry
        // (OST_WeakDims/OST_Dimensions/OST_Constraints). The body's ELEM_CATEGORY_PARAM gives
        // the precise category when present; this is the fallback.
        [0x091F] = -2000260, // OST_Dimensions

        // 0x0E17/0x0FE1/0x0F84/0x06EC/0x09B6 are deliberately absent: those elements are SketchPlanes
        // and other internal classes with no category in the Revit API.

        // Views
        [0x03F8] = -2000279, // OST_Views
        [0x03ED] = -2000279, // OST_Views
        [0x03DA] = -2000279, // OST_Views

        // Roofs
        [0x038C] = -2000035, // OST_Roofs

        // Generic
        [0x0F7E] = -2000080, // OST_GenericModel

        // Classes below come from a census of block classIdx against the category of their owners: only classes
        // whose owners agree on the category are listed. Shared-block classes (0x0702 params, GRep) are
        // excluded because their owners span many categories.
        [0x0D4E] = -2009645, // ReferencePoint: OST_AnalyticalNodes
        [0x0884] = -2009645, // Hub: OST_AnalyticalNodes
        [0x0E49] = -2000263, // SpotDimension: OST_SpotElevations
        // 0x020F (IndependentTag) intentionally absent: a tag's category depends on what
        // it tags, so there is no stable mapping.
        [0x011C] = -2009640, // AnalyticalModelSurface: OST_FloorAnalytical
        [0x0F26] = -2009609, // SunAndShadowSettings: OST_SunStudy
        [0x08F8] = -2000576, // preview legend components
        [0x03F4] = -2000573, // ViewSchedule: OST_Schedules
        [0x0B38] = -2008148, // ParameterElement
        [0x0F27] = -2009609, // sun-path elements
        [0x0855] = -2000055, // building-type elements
        [0x0F83] = -2000300, // TextNote: OST_TextNotes
        [0x0DD9] = -2000278, // OST_Viewers
        [0x0859] = -2008121, // HVAC zone-type elements
        [0x0C01] = -2000924, // PropertySetElement
        [0x0B9D] = -2000191, // filter elements
        [0x009D] = -2000262, // Dimension (constraint form): OST_Constraints
        [0x0B97] = -2008163, // PipeSegment
        [0x02B2] = -2000552, // ColorFillScheme
        [0x0C17] = -2000260, // Dimension: OST_Dimensions
        [0x050A] = -2008143, // ElectricalLoadClassification
        [0x092B] = -2005210, // LoadCase
        [0x0D44] = -2000530, // ReferencePlane: OST_CLines
        [0x0184] = -2003201, // AreaScheme
        [0x0D93] = -2000480, // RoomTag: OST_RoomTags
        [0x014C] = -2000260, // AngularDimension
        [0x0165] = -2000260, // AngularDimension
    };

    /// <summary>
    /// High-purity classId to category map, built from a census of the continuous-walk block classId of every element:
    /// only rows where the sampled owners overwhelmingly share one category are kept. Applied LATE (see
    /// ElementBuilder.FixCategoryFromClassIdx) over the full continuous-walker coverage, so it fills and
    /// corrects categories the early registry never saw. Values that disagree with R22TfClassIdToCategory
    /// (0x0744 FilledRegion not Levels, 0x0DD9 Section not Viewers) are corrections.
    /// </summary>
    public static readonly Dictionary<ushort, int> HighPurityClassCategory = new()
    {
        [0x0D4E] = -2009645, // ReferencePoint
        [0x0884] = -2009645, // Hub
        [0x0DC4] = -2000011, // SWall
        [0x0D93] = -2000480, // RoomTag
        [0x0E49] = -2000263, // SpotElevation
        [0x0F26] = -2009609, // SunAndShadowSettings
        [0x0855] = -2000055, // SketchGrid
        [0x0F83] = -2000300, // TextNote
        [0x0DD9] = -2000279, // Section (registry had -2000278)
        [0x03F8] = -2000279, // DBViewSection
        [0x07C6] = -2000510, // Viewport
        [0x011C] = -2009640, // AnalyticalModelWall
        [0x0F27] = -2009609, // SunAnnotationElem
        [0x0744] = -2002000, // FilledRegion (registry had -2000240 Levels)
        [0x08F8] = -2000576, // LegendComponent
        [0x09B6] = -2000700, // MaterialElem
        [0x03F4] = -2000573, // DBViewSchedule
        [0x0387] = -2000321, // CurtainGridLine
        [0x009D] = -2000262, // Alignment
        [0x0DCC] = -2000570, // ScheduleInstance
        [0x03ED] = -2000279, // DBViewPlan
        [0x0E76] = -2000938, // StairsPathElement
        [0x03E3] = -2003100, // DBViewDrafting
        [0x085A] = -2008119, // HVACLoadSpaceTypeElem
        [0x0779] = -2000032, // Floor
        [0x0D44] = -2000530, // RefPlane
        [0x0282] = -2000038, // Ceiling
        [0x03EC] = -2000279, // DBViewLegend
        [0x083C] = -2000220, // Grid
        [0x0B9D] = -2000191, // PlanRegion
        [0x0B38] = -2008148, // ParamElemElectricalLoadClassification
        [0x0857] = -2008120, // HVACLoadBuildingTypeElem
        [0x0BF9] = -2000112, // ProjectPhase
    };
}

using System.Globalization;

namespace RvtIO.Ifc;

/// <summary>The property-value caches of the current profile, applied once to the
/// finished writer: the entity writers pool every (name, value) pair, but an IfcPropertySingleValue is shared only
/// where its cache accepts the value. Text (label, identifier, text) is cached unless empty, a boolean always, an
/// integer from -10 to 10, a real only when <see cref="CanCacheDoubleCurrent"/> takes it (multiples of 0.05 up to 10, of 0.5 up to
/// 300, of 5 beyond); every other value is a new entity per use.</summary>
public partial class IfcExporter
{
    /// <summary>Whether a real value is cacheable; the tolerance is 1e-10.</summary>
    internal static bool CanCacheDoubleCurrent(double value)
    {
        if (Math.Abs(value) < 1e-10) return true;
        double multiplier = Math.Abs(value) <= 10.0 + 1e-9 ? 0.05 : Math.Abs(value) <= 300.0 + 1e-9 ? 0.5 : 5.0;
        double corrected = Math.Floor(value / multiplier + 1e-9);
        return Math.Abs(value / multiplier - corrected) < 1e-10;
    }

    /// <summary>Whether the entity of this IfcValue literal ("IFCLABEL('x')", "IFCAREAMEASURE(1.5)") is shared.
    /// IfcLengthMeasure is tested in feet, the other measures as written. A literal of another
    /// shape keeps the pooling of the entity writers.</summary>
    internal static bool CachesPropertyValueCurrent(string valueExpr)
    {
        int open = valueExpr.IndexOf('(');
        if (open <= 0 || !valueExpr.EndsWith(')')) return true;
        string type = valueExpr[..open];
        string argument = valueExpr[(open + 1)..^1];
        switch (type)
        {
            case "IFCBOOLEAN": return true;
            case "IFCLABEL" or "IFCIDENTIFIER" or "IFCTEXT": return argument != "''";
            case "IFCINTEGER":
                return !int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer)
                    || integer is >= -10 and <= 10;
            default:
                if (!double.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                    return true;
                return CanCacheDoubleCurrent(type == "IFCLENGTHMEASURE" ? value / 304.8 : value);
        }
    }

    /// <summary>Rewrites the references of every IfcPropertySet to the IfcPropertySingleValue entities the caches
    /// would have created: the cacheable (name, value) pairs share the first entity, every other value gets an entity
    /// per property set (a copy when the pooled one is already taken), and the entities nobody references any
    /// more are removed. Returns the number of IfcPropertySingleValue entities removed.</summary>
    internal static int ApplyCurrentPropertyValueCache(StepWriter w)
    {
        var valueIndexById = new Dictionary<int, int>();
        var setIndexes = new List<int>();
        for (int i = 0; i < w.EntityCount; i++)
        {
            var (id, type, _) = w.EntityAt(i);
            if (type == "IFCPROPERTYSINGLEVALUE") valueIndexById[id] = i;
            else if (type == "IFCPROPERTYSET") setIndexes.Add(i);
        }

        var pooled = new Dictionary<(string Name, string Description, string Value), int>();
        var taken = new HashSet<int>();
        var dropped = new HashSet<int>();
        foreach (int setIndex in setIndexes)
        {
            string[] set = SplitStepArguments(w.EntityAt(setIndex).Args);
            if (set.Length != 5 || set[4].Length < 2 || set[4][0] != '(') continue;
            var references = new List<string>();
            foreach (string reference in set[4][1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (reference[0] != '#'
                    || !int.TryParse(reference.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int valueId)
                    || !valueIndexById.TryGetValue(valueId, out int valueIndex))
                {
                    references.Add(reference);
                    continue;
                }
                string valueArgs = w.EntityAt(valueIndex).Args;
                string[] value = SplitStepArguments(valueArgs);
                int target = valueId;
                if (value.Length != 4 || CachesPropertyValueCurrent(value[2]))
                {
                    if (value.Length == 4 && !pooled.TryAdd((value[0], value[1], value[2]), valueId))
                    {
                        target = pooled[(value[0], value[1], value[2])];
                        if (target != valueId) dropped.Add(valueId);
                    }
                }
                else if (!taken.Add(valueId))
                    target = w.Add("IFCPROPERTYSINGLEVALUE", valueArgs);
                references.Add($"#{target.ToString(CultureInfo.InvariantCulture)}");
            }
            set[4] = $"({string.Join(",", references)})";
            w.ReplaceArgs(setIndex, string.Join(",", set));
        }

        w.RemoveEntities(dropped);
        return dropped.Count;
    }
}

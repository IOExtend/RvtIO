using RvtIO.Model;

namespace RvtIO.Parsing;

internal static partial class ElementBuilder
{
    // shortcut: curved axes retain the geometry fallback until their own symbol curve metrics are decoded.
    internal static void ApplyMullionSymbolLength(
        IReadOnlyList<Element> elements, IReadOnlyDictionary<uint, TfBlockDeserializer.NativeParams> nativeParams,
        Action<Element>? archiveParameters = null)
    {
        foreach (var element in elements)
        {
            if (element.CategoryId != -2000171 || element.InstanceSymbolId is not > 0
                || !nativeParams.TryGetValue((uint)element.InstanceSymbolId.Value, out var symbol)) continue;
            var fields = symbol.DoubleFields;
            const string prefix = "2520:m_pCurve";
            if (!fields.TryGetValue(prefix + ".endParams@0", out double start)
                || !fields.TryGetValue(prefix + ".endParams@1", out double end)) continue;
            double normSquared = 0;
            bool complete = true;
            for (int axis = 0; axis < 3; axis++)
            {
                if (!fields.TryGetValue(prefix + ".dirVec@" + axis, out double direction)
                    || !double.IsFinite(direction)) { complete = false; break; }
                normSquared += direction * direction;
            }
            double length = Math.Abs(end - start) * Math.Sqrt(normSquared);
            if (!complete || !double.IsFinite(length) || length <= 0) continue;
            SetParam(element, -1004005, "CURVE_ELEM_LENGTH", StorageType.Double, length);
            element.Parameters.First(parameter => parameter.Id == -1004005).IsApproximate = false;
            archiveParameters?.Invoke(element);
        }
    }
}

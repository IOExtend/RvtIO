using RvtIO.Internal;

namespace RvtIO.Parsing;

internal static partial class TfBlockDeserializer
{
    // Serialized driver input only: this does not confirm the geometry state.
    internal static CurveDriverData? ExtractOwnWallLocationLine(
        byte[] data, int start, uint size, ushort classId, out string? failure,
        WallCompleteInputRead? complete = null)
    {
        var references = ExtractOwnWallReferenceGeometryInputs(data, start, size, classId, out failure);
        if (references == null) return null;
        failure = "location-driver";
        if (references.CurveDriverPointer is not { ClassId: 4040, QueueIndex: int driverIndex }) return null;
        var drivers = references.CurveDrivers.Where(d => d.QueueIndex == driverIndex).ToArray();
        if (drivers.Length != 1 || drivers[0].ControlData.Handle != 0
            || drivers[0].Curve is not { ClassId: 1736, QueueIndex: int curveIndex }) return null;
        var input = complete == null
            ? ExtractOwnWallCompleteInputs(data, start, size, classId, out failure)
            : complete.Read(out failure);
        if (input == null) return null;
        failure = "location-curve";
        var history = input.Histories.Histories;
        if (history.List.OwnerId != references.OwnerId || history.List.RootEnd != references.RootEnd
            || !history.List.PriorObjects.Concat(history.PriorObjects).Any(o => o.QueueIndex == driverIndex
                && o.ClassId == 4040 && o.Handle == drivers[0].Handle
                && o.Start == drivers[0].Start && o.End == drivers[0].End)) return null;
        var curves = input.LaterObjects.Where(o => o.QueueIndex == curveIndex && o.ClassId == 1736).ToArray();
        if (curves.Length != 1 || curves[0].Handle != drivers[0].Curve.Handle) return null;
        var fields = curves[0].Fields;
        double[]? ReadValues(int declaring, string name, int count)
        {
            var matches = fields.Where(f => f.Declaring == declaring && f.Name == name).ToArray();
            if (matches.Length != 1 || matches[0].Bytes.Length != 8 * count
                || matches[0].End - matches[0].Start != 8 * count
                || matches[0].QueuedObjects.Count != 0) return null;
            return Enumerable.Range(0, count).Select(i => BitConverter.ToDouble(matches[0].Bytes, 8 * i)).ToArray();
        }
        var parameters = ReadValues(1737, "m_endParams", 2);
        var origin = ReadValues(1736, "m_origin", 3);
        var direction = ReadValues(1736, "m_dirVec", 3);
        if (parameters == null || origin == null || direction == null
            || !parameters.Concat(origin).Concat(direction).All(double.IsFinite)
            || parameters[0] == parameters[1] || direction.All(v => v == 0)) return null;
        // Validate the evaluated endpoints too; finite input values may overflow.
        if (parameters.Any(t => Enumerable.Range(0, 3).Any(i => !double.IsFinite(origin[i] + t * direction[i]))))
            return null;
        double lengthSquared = Enumerable.Range(0, 3).Sum(i =>
        {
            double delta = (origin[i] + parameters[1] * direction[i]) - (origin[i] + parameters[0] * direction[i]);
            return delta * delta;
        });
        if (!double.IsFinite(lengthSquared) || lengthSquared == 0) return null;
        failure = null;
        return new CurveDriverData { EndParams = parameters, OriginPoint = origin, DirectionVector = direction };
    }
}

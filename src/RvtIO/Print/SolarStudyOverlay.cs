using System.Globalization;
using System.Numerics;
using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Pdf;

namespace RvtIO.Print;

internal sealed class SolarStudyOverlay
{
    private const double CompassOutside = 1.05;
    private const int DeclinationSteps = 2048;
    private const double Radians = Math.PI / 180;
    private readonly Vector3 _right, _up, _look;
    private readonly double _centerDepth, _modelRadius;
    private readonly double _latitude, _longitude, _zone;
    private readonly (double Start, double End)[][]? _hours;
    private readonly double _minDeclination, _maxDeclination;
    private readonly DateTime _start, _end;
    private readonly DateTime? _active;
    private readonly Vector3? _sun;
    private readonly double? _domeLightFloor;
    private readonly Vector3 _domeHalfVector;
    private readonly int _studyDays;

    internal double X { get; }
    internal double Y { get; }
    internal double Radius { get; }
    internal double GroundElevation { get; }
    internal bool AnnotationVisible { get; }
    internal Vector3? ModelSunDirection { get; }

    private SolarStudyOverlay((double X, double Y, double Depth) center, double radius, double modelRadius,
        double groundElevation, Vector3 right, Vector3 up, Vector3 look, ViewSolarStudy study, RevitFileInfo raw,
        double[] location, bool annotationVisible)
    {
        X = center.X;
        Y = center.Y;
        Radius = radius;
        GroundElevation = groundElevation;
        _centerDepth = center.Depth;
        _modelRadius = modelRadius;
        _right = right;
        _up = up;
        _look = look;
        AnnotationVisible = annotationVisible;
        if (raw.SiteLatitudeRadians is not double latitude || raw.SiteLongitudeRadians is not double longitude
            || raw.SiteTimeZoneHours is not double zone
            || !TryDecodeDate(study.Start, out _start) || !TryDecodeDate(study.End, out _end)
            || _start.Date >= _end.Date || _start.TimeOfDay >= _end.TimeOfDay)
            return;
        (_latitude, _longitude, _zone) = (latitude, longitude, zone);
        // A full leap cycle bounds the annual study paths.
        _studyDays = Math.Min((_end.Date - _start.Date).Days, 4 * 366 + 365);
        if (AnnotationVisible)
            (_hours, _minDeclination, _maxDeclination) = StudyHours();
        // shortcut: hourly frames, until the other stored interval ordinals are verified.
        if (study.Interval != 3 || study.ActiveFrame is not (>= 1 and <= int.MaxValue)
            || study.ActiveFrame != Math.Truncate(study.ActiveFrame)) return;
        int samplesPerDay = 1 + (int)(_end.TimeOfDay - _start.TimeOfDay).TotalHours;
        int index = (int)study.ActiveFrame - 1;
        int day = index / samplesPerDay;
        if (day <= (_end.Date - _start.Date).Days)
        {
            var active = _start.AddDays(day).AddHours(index % samplesPerDay);
            _active = active;
            var (declination, equation) = StudyEphemeris(active, active.TimeOfDay.TotalHours);
            var sun = SunVector(declination, HourAngle(active.TimeOfDay.TotalMinutes, equation));
            _sun = sun;
            ModelSunDirection = ToModelDirection(sun, location);
            if (sun.Z > 0 && study.SunlightIntensity is int intensity)
            {
                _domeLightFloor = LightFloor(sun, intensity);
                var half = sun - look;
                if (half.LengthSquared() > 1e-8f) _domeHalfVector = Vector3.Normalize(half);
            }
        }
    }

    internal static SolarStudyOverlay? Create(RvtDocument document, Element view,
        ViewCropFrame camera, double millimetresPerFoot, Func<double[], (double X, double Y, double Depth)> project)
    {
        var raw = document.RawData;
        if (view.SolarStudy is not { } study || !raw.SharedTransformIsActiveLocation
            || raw.SharedTransform is not { Length: 12 } location
            || document.GetElement(study.GroundLevelId)?.LookupParameter(BuiltInParameter.LEVEL_ELEV)?.Value
                is not double elevation || !double.IsFinite(elevation)) return null;
        double[] box = study.Outline;
        double w = (box[3] - box[0]) / 2, d = (box[4] - box[1]) / 2;
        double h = Math.Max(Math.Abs(box[5] - elevation), Math.Abs(box[2] - elevation));
        double modelRadius = Math.Sqrt(w * w + d * d + h * h) * study.SizePercent / 100;
        double radius = modelRadius * millimetresPerFoot;
        if (!(radius > 0) || !double.IsFinite(radius)) return null;
        var center = project([box[0] + w, box[1] + d, elevation]);
        Vector3 ToShared(double[] v) => new(
            (float)(v[0] * location[0] + v[1] * location[3]),
            (float)(v[0] * location[1] + v[1] * location[4]), (float)v[2]);
        bool visible = study.AnnotationId > 0
            && raw.ViewHiddenElements?.GetValueOrDefault(view.Id)?.Contains(study.AnnotationId) != true;
        if (!visible && study.ShadowIntensity <= 0 && study.SunlightIntensity == null) return null;
        return new(center, radius, modelRadius, elevation, ToShared(camera.Right), ToShared(camera.Up), ToShared(camera.Look),
            study, raw, location, visible);
    }

    internal static Vector3? StillSunDirection(RevitFileInfo raw, int viewId)
    {
        if (raw.ViewStillSunDates?.TryGetValue(viewId, out uint packedDate) != true
            || !TryDecodeDate(packedDate, out var date) || !raw.SharedTransformIsActiveLocation
            || raw.SharedTransform is not { Length: 12 } location
            || raw.SiteLatitudeRadians is not double latitude || raw.SiteLongitudeRadians is not double longitude
            || raw.SiteTimeZoneHours is not double zone) return null;
        var (declination, equation) = Ephemeris(date, date.TimeOfDay.TotalHours);
        double angle = HourAngle(date.TimeOfDay.TotalMinutes, equation, longitude, zone);
        var sun = SunVector(declination, angle, latitude);
        return sun.Z > 0 ? ToModelDirection(sun, location) : null;
    }

    private static Vector3 ToModelDirection(Vector3 sun, double[] location) =>
        new((float)(sun.X * location[0] + sun.Y * location[1]),
            (float)(sun.X * location[3] + sun.Y * location[4]), sun.Z);

    // Calibrated from native shaded output with zero configured ambient light.
    internal static double LightFloor(Vector3 sun, int intensity) => 0.2 + intensity / 100d * sun.Z * sun.Z;

    internal static bool TryDecodeDate(uint value, out DateTime date)
    {
        date = default;
        int year = (int)(value >> 20), month = (int)(value >> 16 & 15), day = (int)(value >> 11 & 31);
        int hour = (int)(value >> 6 & 31), minute = (int)(value & 63);
        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)
            || hour > 23 || minute > 59) return false;
        date = new(year, month, day, hour, minute, 0);
        return true;
    }

    // NOAA's general solar position equations use local standard time and radian angles.
    private static (double Declination, double Equation) Ephemeris(DateTime date, double hour)
    {
        double g = Math.Tau * (date.DayOfYear - 1 + (hour - 12) / 24) / (DateTime.IsLeapYear(date.Year) ? 366 : 365);
        double c = Math.Cos(g), s = Math.Sin(g), c2 = Math.Cos(2 * g), s2 = Math.Sin(2 * g);
        return (0.006918 - 0.399912 * c + 0.070257 * s - 0.006758 * c2 + 0.000907 * s2
            - 0.002697 * Math.Cos(3 * g) + 0.00148 * Math.Sin(3 * g),
            229.18 * (0.000075 + 0.001868 * c - 0.032077 * s - 0.014615 * c2 - 0.040849 * s2));
    }

    private double HourAngle(double minutes, double equation) => HourAngle(minutes, equation, _longitude, _zone);

    private static double HourAngle(double minutes, double equation, double longitude, double zone) =>
        ((minutes + equation - 60 * zone) / 4 - 180) * Radians + longitude;

    // Apparent solar longitude and obliquity from NOAA's Meeus-based solar calculator.
    private (double Declination, double Equation) StudyEphemeris(DateTime date, double hour)
    {
        double t = ((date.Date - new DateTime(2000, 1, 1, 12, 0, 0)).TotalDays + (hour - _zone) / 24) / 36525;
        double longitude = (280.46646 + t * (36000.76983 + 0.0003032 * t)) % 360 * Radians;
        double anomaly = (357.52911 + t * (35999.05029 - 0.0001537 * t)) * Radians;
        double eccentricity = 0.016708634 - t * (0.000042037 + 0.0000001267 * t);
        double center = ((1.914602 - t * (0.004817 + 0.000014 * t)) * Math.Sin(anomaly)
            + (0.019993 - 0.000101 * t) * Math.Sin(2 * anomaly) + 0.000289 * Math.Sin(3 * anomaly)) * Radians;
        double node = (125.04 - 1934.136 * t) * Radians;
        double apparentLongitude = longitude + center - (0.00569 + 0.00478 * Math.Sin(node)) * Radians;
        double obliquity = (23 + (26 + (21.448 - t * (46.815 + t * (0.00059 - 0.001813 * t))) / 60) / 60
            + 0.00256 * Math.Cos(node)) * Radians;
        double y = Math.Tan(obliquity / 2);
        y *= y;
        double equation = y * Math.Sin(2 * longitude) - 2 * eccentricity * Math.Sin(anomaly)
            + 4 * eccentricity * y * Math.Sin(anomaly) * Math.Cos(2 * longitude)
            - 0.5 * y * y * Math.Sin(4 * longitude) - 1.25 * eccentricity * eccentricity * Math.Sin(2 * anomaly);
        return (Math.Asin(Math.Sin(obliquity) * Math.Sin(apparentLongitude)), 4 * equation / Radians);
    }

    private ((double Start, double End)[][] Hours, double Min, double Max) StudyHours()
    {
        var samples = new (double Declination, double Start, double End)[_studyDays + 1];
        for (int i = 0; i < samples.Length; ++i)
        {
            var (declination, equation) = StudyEphemeris(_start.AddDays(i), 12);
            samples[i] = (Math.Sin(declination), HourAngle(_start.TimeOfDay.TotalMinutes, equation),
                HourAngle(_end.TimeOfDay.TotalMinutes, equation));
        }
        double min = samples.Min(s => s.Declination), max = samples.Max(s => s.Declination);
        var hours = new (double Start, double End)[DeclinationSteps + 1][];
        for (int i = 0; i <= DeclinationSteps; ++i)
        {
            double d = min + (max - min) * i / DeclinationSteps;
            var intervals = new List<(double Start, double End)>();
            for (int day = 1; day < samples.Length; ++day)
            {
                var a = samples[day - 1];
                var b = samples[day];
                if (d < Math.Min(a.Declination, b.Declination) || d > Math.Max(a.Declination, b.Declination)) continue;
                double t = a.Declination == b.Declination ? 0 : (d - a.Declination) / (b.Declination - a.Declination);
                intervals.Add((a.Start + (b.Start - a.Start) * t, a.End + (b.End - a.End) * t));
            }
            hours[i] = intervals.ToArray();
        }
        return (hours, min, max);
    }

    private (double X, double Y) Project(double east, double north, double up) =>
        (X + Radius * (east * _right.X + north * _right.Y + up * _right.Z),
         Y + Radius * (east * _up.X + north * _up.Y + up * _up.Z));

    private Vector3 SunVector(double declination, double angle) => SunVector(declination, angle, _latitude);

    private static Vector3 SunVector(double declination, double angle, double latitude)
    {
        double decSin = Math.Sin(declination), decCos = Math.Cos(declination);
        double meridian = decCos * Math.Cos(angle), sinLat = Math.Sin(latitude), cosLat = Math.Cos(latitude);
        return new((float)(-decCos * Math.Sin(angle)), (float)(cosLat * decSin - sinLat * meridian),
            (float)(sinLat * decSin + cosLat * meridian));
    }

    private (double X, double Y) ProjectSun(double declination, double angle)
    {
        var vector = SunVector(declination, angle);
        return Project(vector.X, vector.Y, vector.Z);
    }

    private static bool SameCalendarDay(DateTime a, DateTime b) => a.Month == b.Month && a.Day == b.Day;

    internal void Paint(byte[] rgb, int columns, int rows, double left, double top, double dx, double dy,
        double[]? modelDepth = null)
    {
        double determinant = _right.X * _up.Y - _right.Y * _up.X;
        double sinLat = Math.Sin(_latitude), cosLat = Math.Cos(_latitude);
        double pen = Math.Max(dx, dy) / Radius * 0.6;
        for (int row = 0; row < rows; ++row)
        {
            double v = (top - (row + 0.5) * dy - Y) / Radius;
            for (int col = 0; col < columns; ++col)
            {
                double u = (left + (col + 0.5) * dx - X) / Radius;
                int pixel = 3 * (row * columns + col);
                bool hasModel = rgb[pixel] != 255 || rgb[pixel + 1] != 255 || rgb[pixel + 2] != 255;
                if (Math.Abs(determinant) > 1e-6)
                {
                    double east = (_up.Y * u - _right.Y * v) / determinant;
                    double north = (_right.X * v - _up.X * u) / determinant;
                    double r = Math.Sqrt(east * east + north * north);
                    double compassDepth = _centerDepth + _modelRadius * (east * _look.X + north * _look.Y);
                    if (r <= 1.09 && (modelDepth == null || compassDepth <= modelDepth[pixel / 3] + 1e-5))
                    {
                        double degrees = Math.Atan2(-east, north) / Radians;
                        double sector = (degrees + 360) % 30;
                        double tick = Math.Abs(sector - Math.Round(sector / 5) * 5);
                        bool major = Math.Abs(sector - Math.Round(sector / 15) * 15) * Radians * r < pen;
                        if (r >= 1 && r <= CompassOutside)
                            Blend(pixel, sector < 15 ? 138 : 250, sector < 15 ? 138 : 250,
                                sector < 15 ? 138 : 250, sector < 15 ? 1 : 0.45);
                        bool circle = Math.Abs(r - 1) < pen || Math.Abs(r - CompassOutside) < pen;
                        bool onTick = (major && r >= 0.97 && r <= 1.08)
                            || (sector >= 15 && r >= 1 && r <= CompassOutside && tick * Radians * r < pen);
                        if (circle || onTick) Blend(pixel, 115, 115, 115, 0.53);
                        if (rgb[pixel] == 255 && rgb[pixel + 1] == 255 && rgb[pixel + 2] == 255
                            && Math.Min(Math.Abs(east), Math.Abs(north)) < pen)
                            Blend(pixel, 170, 170, 170, 0.4);
                    }
                }
                if (_hours == null || u * u + v * v >= 1) continue;
                double depth = Math.Sqrt(1 - u * u - v * v);
                bool front = InBand(u, v, -depth, sinLat, cosLat, out var direction);
                bool back = InBand(u, v, depth, sinLat, cosLat, out _);
                double incidence = _sun is Vector3 sun && front ? Vector3.Dot(sun, direction) : 1;
                if (front && !back && _domeLightFloor is double lightFloor)
                {
                    // shortcut: native single-surface tint, until overlapping band layers are verified.
                    double highlight = incidence > 0 ? Math.Max(0, Vector3.Dot(_domeHalfVector, direction)) : 0;
                    double light = lightFloor + Math.Max(0, incidence) + 0.5 * Math.Pow(highlight, 64);
                    Blend(pixel, (int)Math.Min(254, Math.Round(255 * light)),
                        (int)Math.Min(254, Math.Round(160 * light)), 0, 0.75);
                    continue;
                }
                bool unlitBackground = front && !hasModel && incidence <= 0;
                if (front && !unlitBackground)
                {
                    // shortcut: diffuse dome shading, until the native study shader is reproduced.
                    double diffuse = Math.Max(0, incidence);
                    double intensity = hasModel ? Math.Min(1, 0.38 + diffuse) : 1;
                    double green = hasModel ? 175 + 80 * Math.Pow(diffuse, 6) : 165 + 70 * direction.Z;
                    Blend(pixel, (int)(255 * intensity), (int)(green * intensity), 6, 0.74);
                }
                if (back || unlitBackground) Blend(pixel, 135, 105, 62, front && !unlitBackground ? 0.63 : 1);
            }
        }
        if (_hours != null)
        {
            PaintGuide(TimeBoundary(_start.TimeOfDay.TotalMinutes));
            PaintGuide(TimeBoundary(_end.TimeOfDay.TotalMinutes));
            PaintGuide(HorizonBoundary(-1));
            PaintGuide(HorizonBoundary(1));
            PaintGuide(DayGuide(_start));
            if (!SameCalendarDay(_start, _end)) PaintGuide(DayGuide(_end));
            if (_sun is Vector3 sun)
            {
                var center = Project(sun.X, sun.Y, sun.Z);
                double sunRadius = Radius * 0.05;
                int fromRow = Math.Max(0, (int)Math.Floor((top - center.Y - sunRadius) / dy));
                int toRow = Math.Min(rows - 1, (int)Math.Ceiling((top - center.Y + sunRadius) / dy));
                int fromCol = Math.Max(0, (int)Math.Floor((center.X - sunRadius - left) / dx));
                int toCol = Math.Min(columns - 1, (int)Math.Ceiling((center.X + sunRadius - left) / dx));
                for (int row = fromRow; row <= toRow; ++row)
                    for (int col = fromCol; col <= toCol; ++col)
                    {
                        double a = (left + (col + 0.5) * dx - center.X) / sunRadius;
                        double b = (top - (row + 0.5) * dy - center.Y) / sunRadius;
                        double r2 = a * a + b * b;
                        if (r2 <= 1) Blend(3 * (row * columns + col), 255, r2 < 0.88 ? 245 : 175,
                            r2 < 0.88 ? 91 : 11, 0.85);
                    }
            }
        }

        IEnumerable<Vector3?> DayGuide(DateTime day)
        {
            var (dec, _) = StudyEphemeris(day, 12);
            var horizon = Horizon(dec, 0);
            if (horizon <= 0) yield break;
            int samples = (int)Math.Ceiling(4 * horizon / Radians);
            for (int i = 0; i <= samples; ++i)
                yield return SunVector(dec, horizon * (2.0 * i / samples - 1));
        }

        IEnumerable<Vector3?> TimeBoundary(double minutes)
        {
            for (int day = 0; day <= _studyDays; day++)
            {
                var (dec, equation) = StudyEphemeris(_start.AddDays(day), minutes / 60);
                yield return SunVector(dec, HourAngle(minutes, equation));
            }
        }

        IEnumerable<Vector3?> HorizonBoundary(int sign)
        {
            for (int i = 0; i <= DeclinationSteps; i++)
            {
                double dec = Math.Asin(_minDeclination + (_maxDeclination - _minDeclination) * i / DeclinationSteps);
                double horizon = Horizon(dec, 0);
                yield return horizon is > 0 and < Math.PI && ContainsStudyHour(i, sign * horizon)
                    ? SunVector(dec, sign * horizon) with { Z = 0 } : null;
            }
        }

        void PaintGuide(IEnumerable<Vector3?> vertices)
        {
            Vector3? previous = null;
            int previousPixel = -1;
            foreach (var vertex in vertices)
            {
                if (previous is Vector3 a && vertex is Vector3 b && Math.Max(a.Z, b.Z) >= -1e-7f)
                {
                    if (Math.Min(a.Z, b.Z) < -1e-7f)
                    {
                        var horizon = Vector3.Lerp(a, b, a.Z / (a.Z - b.Z));
                        if (a.Z < 0) a = horizon;
                        else b = horizon;
                    }
                    var from = Project(a.X, a.Y, a.Z);
                    var to = Project(b.X, b.Y, b.Z);
                    double vx = (to.X - from.X) / dx, vy = (from.Y - to.Y) / dy;
                    double x = (from.X - left) / dx, y = (top - from.Y) / dy;
                    int samples = (int)Math.Clamp(Math.Ceiling(2 * Math.Max(Math.Abs(vx), Math.Abs(vy))), 1, 30_000);
                    for (int i = 0; i <= samples; i++)
                    {
                        double t = i / (double)samples;
                        int col = (int)Math.Floor(x + t * vx), row = (int)Math.Floor(y + t * vy);
                        if (col < 0 || col >= columns || row < 0 || row >= rows) continue;
                        int pixel = 3 * (row * columns + col);
                        if (pixel == previousPixel) continue;
                        Blend(pixel, 238, 147, 12, 0.85);
                        previousPixel = pixel;
                    }
                }
                previous = vertex;
            }
        }

        void Blend(int p, int r, int g, int b, double opacity)
        {
            rgb[p] = (byte)Math.Min(254, Math.Round(rgb[p] + (r - rgb[p]) * opacity));
            rgb[p + 1] = (byte)Math.Min(254, Math.Round(rgb[p + 1] + (g - rgb[p + 1]) * opacity));
            rgb[p + 2] = (byte)Math.Min(254, Math.Round(rgb[p + 2] + (b - rgb[p + 2]) * opacity));
        }
    }

    private bool InBand(double u, double v, double depth, double sinLat, double cosLat, out Vector3 direction)
    {
        direction = default;
        double height = u * _right.Z + v * _up.Z + depth * _look.Z;
        if (_hours == null || height < 0) return false;
        double north = u * _right.Y + v * _up.Y + depth * _look.Y;
        double declination = north * cosLat + height * sinLat;
        if (declination < _minDeclination || declination > _maxDeclination || !(_maxDeclination > _minDeclination))
            return false;
        double east = u * _right.X + v * _up.X + depth * _look.X;
        direction = new((float)east, (float)north, (float)height);
        double hour = Math.Atan2(-east, height * cosLat - north * sinLat);
        int row = (int)Math.Round((declination - _minDeclination) / (_maxDeclination - _minDeclination) * DeclinationSteps);
        return ContainsStudyHour(row, hour);
    }

    private bool ContainsStudyHour(int row, double hour)
    {
        foreach (var (from, to) in _hours![row])
            if (hour >= from && hour <= to || hour + Math.Tau >= from && hour + Math.Tau <= to
                || hour - Math.Tau >= from && hour - Math.Tau <= to) return true;
        return false;
    }

    private double Horizon(double declination, double altitude) => Math.Acos(Math.Clamp(
        (Math.Sin(altitude) - Math.Sin(_latitude) * Math.Sin(declination)) /
        (Math.Cos(_latitude) * Math.Cos(declination)), -1, 1));

    internal void DrawLabels(PdfPage page)
    {
        bool russian = page.Owner.Font != null && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
        Cardinal(russian ? "С" : "N", 0, 1.15);
        Cardinal(russian ? "В" : "E", 1.15, 0);
        Cardinal(russian ? "Ю" : "S", 0, -1.15);
        Cardinal(russian ? "З" : "W", -1.15, 0);
        if (_hours == null) return;
        var ink = new PdfColor(0.94, 0.49, 0.01);
        var timeDay = _active?.Date ?? _start.Date;
        bool sharedDate = SameCalendarDay(_start, _end);
        DateLabel(_start, bold: sharedDate);
        if (!sharedDate) DateLabel(_end, bold: false);
        if (!SameCalendarDay(timeDay, _start) && !SameCalendarDay(timeDay, _end)) DateLabel(timeDay, bold: false);
        foreach (double minutes in new[] { _start.TimeOfDay.TotalMinutes, _end.TimeOfDay.TotalMinutes })
        {
            var (declination, equation) = StudyEphemeris(timeDay, minutes / 60);
            TimeLabel(ProjectSun(declination, HourAngle(minutes, equation)), minutes, ink);
        }
        if (_active is { } active && _sun is Vector3 sun)
        {
            DrawHorizonTimes(active);
            var (x, y) = Project(sun.X, sun.Y, sun.Z);
            TimeLabel((x - 4, y + Radius * 0.075), active.TimeOfDay.TotalMinutes, new PdfColor(0.85, 0.28, 0.08));
        }

        void Cardinal(string value, double east, double north)
        {
            var point = Project(east, north, 0);
            page.DrawText(value, point.X, point.Y - 1, 4, PdfTextAlignment.Center);
        }

        void DateLabel(DateTime day, bool bold)
        {
            var (dec, _) = StudyEphemeris(day, 12);
            double horizon = Horizon(dec, 0);
            if (!(horizon > 0 && horizon < Math.PI)) return;
            var rise = ProjectSun(dec, -horizon);
            var culture = russian ? CultureInfo.GetCultureInfo("ru-RU") : CultureInfo.GetCultureInfo("en-US");
            page.DrawStyledText(day.ToString(russian ? "d MMMM" : "MMM d", culture), rise.X + 1, rise.Y - 4,
                3.4, PdfTextAlignment.Left, 0, bold, ink);
        }

        void DrawHorizonTimes(DateTime day)
        {
            var (dec, equation) = StudyEphemeris(day, 12);
            double horizon = Horizon(dec, 0);
            if (!(horizon > 0 && horizon < Math.PI)) return;
            double noon = 720 - equation - 4 * _longitude / Radians + 60 * _zone;
            TimeLabel(ProjectSun(dec, -horizon), Math.Ceiling(noon - 4 * horizon / Radians), ink);
            TimeLabel(ProjectSun(dec, horizon), Math.Floor(noon + 4 * horizon / Radians), new PdfColor(1, 0.18, 0.2));
        }

        void TimeLabel((double X, double Y) point, double minutes, PdfColor ink)
        {
            int time = ((int)Math.Round(minutes) % 1440 + 1440) % 1440;
            page.DrawStyledText(FormattableString.Invariant($"{time / 60}:{time % 60:00}"),
                point.X + 1, point.Y + 1, 2.6, PdfTextAlignment.Left, 0, false, ink);
        }
    }
}

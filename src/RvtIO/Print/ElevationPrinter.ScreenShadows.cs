using RvtIO.Pdf;
using Point = (double X, double Y);

namespace RvtIO.Print;

public static partial class ElevationPrinter
{
    internal sealed class PaneScreenShadows(
        (double X0, double Y0, double X1, double Y1) crop, int scale, double ambientFraction)
    {
        // Screen-space approximation calibrated on a native wall/pane pair at 1:20,
        // with depth offsets and scales 1:30 and 1:40 checked separately.
        private const double PaperRadius = 125;
        private const double DepthPerScale = 600;
        private const double DisplayGain = 247;
        private const int SampleCount = 512;
        private const int MaxGridSide = 1024;
        private const float DepthTolerance = 0.1f;

        internal IReadOnlyDictionary<List<Point>, AmbientPatch>? Calculate(
            List<(List<Point> Points, double Depth, bool Outline)> faces,
            IReadOnlyList<int> owners,
            IReadOnlyDictionary<List<Point>, List<List<Point>>> openings,
            HashSet<int> paneOwners,
            Dictionary<List<Point>, double> receivers,
            IReadOnlyDictionary<List<Point>, AmbientPatch>? localTones)
        {
            if (paneOwners.Count == 0 || receivers.Count == 0 || scale <= 0 || ambientFraction <= 0)
                return null;
            double spanX = crop.X1 - crop.X0, spanY = crop.Y1 - crop.Y0;
            if (!(spanX > 0 && spanY > 0) || !double.IsFinite(spanX) || !double.IsFinite(spanY))
                return null;
            double step = Math.Max(scale * 25.4 / 72, Math.Max(spanX, spanY) / MaxGridSide);
            // Keep the last pixel centres inside the same saved crop as the PDF.
            int width = Math.Clamp((int)Math.Floor(spanX / step + 0.5), 1, MaxGridSide);
            int height = Math.Clamp((int)Math.Floor(spanY / step + 0.5), 1, MaxGridSide);
            double X(int i) => crop.X0 + (i + 0.5) * step;
            double Y(int j) => crop.Y0 + (j + 0.5) * step;
            int FirstX(double value) => Math.Clamp((int)Math.Ceiling((value - crop.X0) / step - 0.5), 0, width);
            int FirstY(double value) => Math.Clamp((int)Math.Ceiling((value - crop.Y0) / step - 0.5), 0, height);

            var withPanes = new float[width * height];
            var withoutPanes = new float[withPanes.Length];
            Array.Fill(withPanes, float.PositiveInfinity);
            Array.Fill(withoutPanes, float.PositiveInfinity);
            var crossings = new List<double>();
            foreach (int index in Enumerable.Range(0, faces.Count).OrderByDescending(i => faces[i].Depth))
            {
                var (ring, depth, _) = faces[index];
                var holes = openings.GetValueOrDefault(ring);
                bool pane = paneOwners.Contains(owners[index]);
                int bottom = FirstY(ring.Min(p => p.Y)), top = FirstY(ring.Max(p => p.Y));
                for (int j = bottom; j < top; j++)
                {
                    double y = Y(j);
                    crossings.Clear();
                    AddCrossings(ring, y, crossings);
                    if (holes != null)
                        foreach (var hole in holes) AddCrossings(hole, y, crossings);
                    crossings.Sort();
                    for (int k = 0; k + 1 < crossings.Count; k += 2)
                    {
                        int left = FirstX(crossings[k]), right = FirstX(crossings[k + 1]);
                        if (left >= right) continue;
                        withPanes.AsSpan(j * width + left, right - left).Fill((float)depth);
                        if (!pane) withoutPanes.AsSpan(j * width + left, right - left).Fill((float)depth);
                    }
                }
            }

            double pixelRadius = PaperRadius * scale / step;
            float depthRadius = (float)(DepthPerScale * scale);
            var samples = new (int X, int Y, float Height)[SampleCount];
            float totalHeight = 0;
            double goldenAngle = Math.PI * (3 - Math.Sqrt(5));
            for (int i = 0; i < samples.Length; i++)
            {
                double part = (i + 0.5) / samples.Length;
                double radius = pixelRadius * Math.Sqrt(part), angle = (i + 0.5) * goldenAngle;
                float sampleHeight = (float)Math.Sqrt(1 - part);
                samples[i] = ((int)Math.Round(radius * Math.Cos(angle)),
                    (int)Math.Round(radius * Math.Sin(angle)), sampleHeight);
                totalHeight += sampleHeight;
            }

            var result = new Dictionary<List<Point>, AmbientPatch>();
            foreach (var (wall, depth) in receivers)
            {
                int left = FirstX(wall.Min(p => p.X)), right = FirstX(wall.Max(p => p.X));
                int bottom = FirstY(wall.Min(p => p.Y)), top = FirstY(wall.Max(p => p.Y));
                int w = right - left, h = top - bottom;
                if (w <= 0 || h <= 0) continue;
                var pixels = new byte[w * h * 3];
                Array.Fill(pixels, (byte)255);
                bool changed = false;
                var local = localTones?.GetValueOrDefault(wall);
                float wallDepth = (float)depth;
                for (int j = bottom; j < top; j++)
                for (int i = left; i < right; i++)
                {
                    int pixel = j * width + i;
                    if (Math.Abs(withPanes[pixel] - wallDepth) > DepthTolerance
                        || Math.Abs(withoutPanes[pixel] - wallDepth) > DepthTolerance)
                        continue;
                    float added = 0;
                    foreach (var (dx, dy, heightAtSample) in samples)
                    {
                        int x = i + dx, y = j + dy;
                        if ((uint)x >= (uint)width || (uint)y >= (uint)height) continue;
                        int tap = y * width + x;
                        if (withPanes[tap] == withoutPanes[tap]) continue;
                        added += Occupancy((wallDepth - withPanes[tap]) / depthRadius, heightAtSample)
                            - Occupancy((wallDepth - withoutPanes[tap]) / depthRadius, heightAtSample);
                    }
                    if (added <= 0) continue;
                    double original = LocalGray(local, X(i), Y(j));
                    double originalLinear = ToLinear(original);
                    double available = Math.Max(0, ambientFraction - (1 - originalLinear));
                    double screenLoss = 1 - ToLinear(1 - Math.Min(DisplayGain / 255 * added / totalHeight, 1));
                    byte gray = AmbientOcclusion.ToTone(originalLinear - screenLoss * available / ambientFraction);
                    if (gray >= Math.Round(original * 255)) continue;
                    int destination = ((top - 1 - j) * w + i - left) * 3;
                    pixels[destination] = pixels[destination + 1] = pixels[destination + 2] = gray;
                    changed = true;
                }
                if (changed)
                    result.Add(wall, new(new PdfRasterImage(w, h, pixels) { TransparentWhite = true },
                        crop.X0 + left * step, crop.Y0 + bottom * step,
                        crop.X0 + right * step, crop.Y0 + top * step));
            }
            return result.Count > 0 ? result : null;
        }

        private static void AddCrossings(List<Point> ring, double y, List<double> crossings)
        {
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                var a = ring[j];
                var b = ring[i];
                if ((a.Y > y) != (b.Y > y))
                    crossings.Add(a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
            }
        }

        private static float Occupancy(float depth, float height)
            => depth is > 0 and < 1 ? Math.Min(depth, height) * (1 - depth * depth) : 0;

        private static double ToLinear(double gray)
            => gray <= 0.04045 ? gray / 12.92 : Math.Pow((gray + 0.055) / 1.055, 2.4);

        private static double LocalGray(AmbientPatch? tone, double x, double y)
        {
            if (tone == null) return 1;
            var image = tone.Image;
            double u = Math.Clamp((x - tone.X0) / (tone.X1 - tone.X0) * image.Width - 0.5, 0, image.Width - 1);
            double v = Math.Clamp((tone.Y1 - y) / (tone.Y1 - tone.Y0) * image.Height - 0.5, 0, image.Height - 1);
            int i = (int)u, j = (int)v;
            int nextX = Math.Min(i + 1, image.Width - 1), nextY = Math.Min(j + 1, image.Height - 1);
            double Pixel(int a, int b) => image.Rgb[(b * image.Width + a) * 3];
            double top = Pixel(i, j) + (Pixel(nextX, j) - Pixel(i, j)) * (u - i);
            double bottom = Pixel(i, nextY) + (Pixel(nextX, nextY) - Pixel(i, nextY)) * (u - i);
            return (top + (bottom - top) * (v - j)) / 255;
        }
    }
}

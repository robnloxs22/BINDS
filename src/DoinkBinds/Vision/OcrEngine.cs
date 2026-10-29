using System.Reflection;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DoinkBinds.Vision;

/// <summary>A line of recognised text, in pixel coordinates of the image that was read.</summary>
public sealed record OcrLine(string Text, double Confidence, int X, int Y, int Width, int Height);

public interface IOcrEngine
{
    /// <summary>Reads all text in an image. Throws <see cref="InvalidOperationException"/> if the engine can't load.</summary>
    IReadOnlyList<OcrLine> Recognize(BgraImage image);
}

/// <summary>
/// Bundled, offline OCR: PaddleOCR PP-OCRv4 (detection + recognition) running on ONNX Runtime.
/// Ported from RapidOCR's pre/post-processing (Apache-2.0). Screen text is upright, so the
/// angle classifier is skipped, and boxes are axis-aligned connected components.
/// </summary>
public sealed class PaddleOcrEngine : IOcrEngine, IDisposable
{
    // RapidOCR defaults
    private const int DetLimitSideLen = 736;
    private const int DetMaxSideLen = 2000;
    private const float DetThresh = 0.3f;
    private const float DetBoxThresh = 0.5f;
    private const float UnclipRatio = 1.6f;
    private const int RecHeight = 48;
    private const int RecMinWidth = 320;
    private const double TextScoreThresh = 0.5;

    private readonly object _gate = new();
    private InferenceSession? _det, _rec;
    private string[] _chars = Array.Empty<string>();

    private void EnsureLoaded()
    {
        if (_det is not null && _rec is not null) return;
        var asm = Assembly.GetExecutingAssembly();
        byte[] Load(string name)
        {
            using var s = asm.GetManifestResourceStream(name)
                          ?? throw new InvalidOperationException($"OCR model '{name}' is missing from the build.");
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        };
        _det = new InferenceSession(Load("DoinkBinds.Ocr.det.onnx"), options);
        _rec = new InferenceSession(Load("DoinkBinds.Ocr.rec.onnx"), options);

        // The recogniser's character set is stored in the model metadata ("character", one per line).
        if (!_rec.ModelMetadata.CustomMetadataMap.TryGetValue("character", out var dict))
            throw new InvalidOperationException("OCR model has no character dictionary.");
        var list = new List<string> { "" }; // index 0 = CTC blank
        list.AddRange(dict.Split('\n').Select(l => l.TrimEnd('\r')));
        list.Add(" ");
        _chars = list.ToArray();
    }

    public IReadOnlyList<OcrLine> Recognize(BgraImage image)
    {
        lock (_gate) // ONNX sessions are thread-safe, but serialising keeps CPU use predictable
        {
            EnsureLoaded();

            // Small single-line regions (a button label, a score): skip detection entirely.
            if (image.Height <= 80 && image.Width >= image.Height)
            {
                var (text, conf) = RecognizeLine(image);
                return conf >= TextScoreThresh && text.Trim().Length > 0
                    ? new[] { new OcrLine(text.Trim(), conf, 0, 0, image.Width, image.Height) }
                    : Array.Empty<OcrLine>();
            }

            var lines = new List<OcrLine>();
            foreach (var (x, y, w, h) in Detect(image))
            {
                var crop = image.Crop(x, y, w, h);
                var (text, conf) = RecognizeLine(crop);
                if (conf >= TextScoreThresh && text.Trim().Length > 0)
                    lines.Add(new OcrLine(text.Trim(), conf, x, y, w, h));
            }

            // Reading order: rows top-to-bottom (lines whose centres are within ~half a line height
            // share a row), then left-to-right.
            if (lines.Count < 2) return lines;
            var rowHeight = Math.Max(8, lines.Select(l => l.Height).OrderBy(v => v).ElementAt(lines.Count / 2) * 0.6);
            return lines
                .OrderBy(l => (int)Math.Round((l.Y + l.Height / 2.0) / rowHeight))
                .ThenBy(l => l.X)
                .ToList();
        }
    }

    // ------------------------------------------------------------------ detection

    private List<(int X, int Y, int W, int H)> Detect(BgraImage image)
    {
        int h = image.Height, w = image.Width;
        var ratio = 1.0;
        if (Math.Min(h, w) < DetLimitSideLen) ratio = (double)DetLimitSideLen / Math.Min(h, w);
        if (Math.Max(h, w) * ratio > DetMaxSideLen) ratio = (double)DetMaxSideLen / Math.Max(h, w);
        var rh = Math.Max(32, (int)Math.Round(h * ratio / 32) * 32);
        var rw = Math.Max(32, (int)Math.Round(w * ratio / 32) * 32);

        var resized = image.Resize(rw, rh);
        var input = ToTensor(resized, rw, rh, rw);

        var inputName = _det!.InputMetadata.Keys.First();
        using var results = _det.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, input) });
        var pred = results.First().AsTensor<float>();
        var ph = pred.Dimensions[2];
        var pw = pred.Dimensions[3];
        var prob = pred.ToArray();

        // Binarise + 2×2 dilation (as RapidOCR does).
        var mask = new bool[ph * pw];
        for (var i = 0; i < prob.Length; i++) mask[i] = prob[i] > DetThresh;
        var dil = new bool[ph * pw];
        for (var y = 0; y < ph; y++)
        for (var x = 0; x < pw; x++)
        {
            if (!mask[y * pw + x]) continue;
            dil[y * pw + x] = true;
            if (x + 1 < pw) dil[y * pw + x + 1] = true;
            if (y + 1 < ph) dil[(y + 1) * pw + x] = true;
            if (x + 1 < pw && y + 1 < ph) dil[(y + 1) * pw + x + 1] = true;
        }

        var boxes = new List<(int, int, int, int)>();
        var labels = new int[ph * pw];
        var stack = new Stack<int>();
        var label = 0;
        for (var start = 0; start < dil.Length; start++)
        {
            if (!dil[start] || labels[start] != 0) continue;
            label++;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = 0, maxY = 0;
            stack.Push(start);
            labels[start] = label;
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                var px = p % pw;
                var py = p / pw;
                if (px < minX) minX = px;
                if (px > maxX) maxX = px;
                if (py < minY) minY = py;
                if (py > maxY) maxY = py;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    var nx = px + dx;
                    var ny = py + dy;
                    if (nx < 0 || ny < 0 || nx >= pw || ny >= ph) continue;
                    var q = ny * pw + nx;
                    if (dil[q] && labels[q] == 0)
                    {
                        labels[q] = label;
                        stack.Push(q);
                    }
                }
            }

            var bw = maxX - minX + 1;
            var bh = maxY - minY + 1;
            if (Math.Min(bw, bh) < 3) continue;

            // "fast" score: mean probability inside the bounding box.
            double sum = 0;
            for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
                sum += prob[y * pw + x];
            if (sum / (bw * bh) < DetBoxThresh) continue;

            // Unclip: expand by area × ratio / perimeter.
            var d = bw * bh * UnclipRatio / (2.0 * (bw + bh));
            var ex0 = minX - d;
            var ey0 = minY - d;
            var ex1 = maxX + 1 + d;
            var ey1 = maxY + 1 + d;
            if (Math.Min(ex1 - ex0, ey1 - ey0) < 5) continue;

            // Back to original image pixels.
            var sx = (double)w / pw;
            var sy = (double)h / ph;
            var ox = (int)Math.Clamp(Math.Floor(ex0 * sx), 0, w - 1);
            var oy = (int)Math.Clamp(Math.Floor(ey0 * sy), 0, h - 1);
            var ox1 = (int)Math.Clamp(Math.Ceiling(ex1 * sx), ox + 1, w);
            var oy1 = (int)Math.Clamp(Math.Ceiling(ey1 * sy), oy + 1, h);
            boxes.Add((ox, oy, ox1 - ox, oy1 - oy));
        }
        return boxes;
    }

    // ------------------------------------------------------------------ recognition

    private (string Text, double Confidence) RecognizeLine(BgraImage crop)
    {
        var ratio = (double)crop.Width / crop.Height;
        var resizedW = Math.Max(1, (int)Math.Ceiling(RecHeight * ratio));
        var tensorW = Math.Max(RecMinWidth, resizedW);
        var resized = crop.Resize(resizedW, RecHeight);
        var input = ToTensor(resized, resizedW, RecHeight, tensorW);

        var inputName = _rec!.InputMetadata.Keys.First();
        using var results = _rec.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, input) });
        var pred = results.First().AsTensor<float>();
        var steps = pred.Dimensions[1];
        var classes = pred.Dimensions[2];
        var data = pred.ToArray();

        var sb = new System.Text.StringBuilder();
        double confSum = 0;
        var confCount = 0;
        var last = -1;
        for (var t = 0; t < steps; t++)
        {
            var best = 0;
            var bestP = float.MinValue;
            var row = t * classes;
            for (var c = 0; c < classes; c++)
            {
                if (data[row + c] > bestP) { bestP = data[row + c]; best = c; }
            }
            // CTC greedy decode: drop blanks and repeats.
            if (best != 0 && best != last && best < _chars.Length)
            {
                sb.Append(_chars[best]);
                confSum += bestP;
                confCount++;
            }
            last = best;
        }
        return (sb.ToString(), confCount == 0 ? 0 : confSum / confCount);
    }

    /// <summary>BGR, (v / 255 − 0.5) / 0.5, NCHW. Columns beyond <paramref name="w"/> stay 0 (padding).</summary>
    private static DenseTensor<float> ToTensor(BgraImage img, int w, int h, int tensorWidth)
    {
        var t = new DenseTensor<float>(new[] { 1, 3, h, tensorWidth });
        var px = img.Pixels;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * img.Width + x) * 4;
                t[0, 0, y, x] = px[i] / 127.5f - 1f;     // B
                t[0, 1, y, x] = px[i + 1] / 127.5f - 1f; // G
                t[0, 2, y, x] = px[i + 2] / 127.5f - 1f; // R
            }
        }
        return t;
    }

    public void Dispose()
    {
        _det?.Dispose();
        _rec?.Dispose();
    }
}

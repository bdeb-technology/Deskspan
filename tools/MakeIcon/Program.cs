using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Builds Deskspan.ico (16–256) and Deskspan.png from the generated mark.
var root = args.Length > 0 ? args[0] : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
var source = args.Length > 1 ? args[1] : throw new ArgumentException("Pass the generated logo image path.");
var destDir = Path.Combine(root, "src", "Deskspan.App", "Assets");
Directory.CreateDirectory(destDir);

using var original = (Bitmap)Image.FromFile(source);
using var tile = PunchRoundedCorners(original);
using (var preview = Scale(tile, 256))
    preview.Save(Path.Combine(destDir, "Deskspan.png"), ImageFormat.Png);

var frames = new List<(int Size, byte[] Png)>();
foreach (var size in new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 })
{
    using var frame = size <= 32 ? DrawCrisp(size) : Scale(tile, size);
    frames.Add((size, ToPng(frame)));
}

WriteIco(Path.Combine(destDir, "Deskspan.ico"), frames);
Console.WriteLine("Wrote " + destDir);

static Bitmap PunchRoundedCorners(Bitmap source)
{
    var w = source.Width;
    var h = source.Height;
    var dest = new Bitmap(w, h, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(dest))
    {
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var path = RoundedRect(0.5f, 0.5f, w - 1f, h - 1f, w * 0.22f);
        g.SetClip(path);
        g.DrawImage(source, 0, 0, w, h);
    }

    return dest;
}

static Bitmap Scale(Bitmap source, int size)
{
    var dest = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(dest);
    g.Clear(Color.Transparent);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.DrawImage(source, 0, 0, size, size);
    return dest;
}

static Bitmap DrawCrisp(int size)
{
    var dest = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(dest);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);
    var pad = size <= 16 ? 0.6f : 0.8f;
    using (var tile = new SolidBrush(Color.FromArgb(12, 17, 22)))
    using (var path = RoundedRect(pad, pad, size - pad * 2, size - pad * 2, size * 0.22f))
        g.FillPath(tile, path);

    using var mark = new SolidBrush(Color.FromArgb(94, 234, 212));
    var s = size;
    // Up-pointing location arrow that matches the generated logo at tiny sizes.
    var cx = s * 0.50f;
    var top = s * 0.18f;
    var bottom = s * 0.78f;
    var stemW = Math.Max(1.6f, s * 0.16f);
    var headW = s * 0.46f;
    var headH = s * 0.38f;
    var notch = s * 0.10f;
    using var arrow = new GraphicsPath();
    arrow.AddPolygon(new[]
    {
        new PointF(cx, top),
        new PointF(cx + headW * 0.5f, top + headH),
        new PointF(cx + notch, top + headH - s * 0.04f),
        new PointF(cx + stemW * 0.5f, bottom),
        new PointF(cx - stemW * 0.5f, bottom),
        new PointF(cx - notch, top + headH - s * 0.04f),
        new PointF(cx - headW * 0.5f, top + headH)
    });
    g.FillPath(mark, arrow);
    return dest;
}

static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
{
    r = Math.Min(r, Math.Min(w, h) / 2f);
    var path = new GraphicsPath();
    var d = r * 2;
    path.AddArc(x, y, d, d, 180, 90);
    path.AddArc(x + w - d, y, d, d, 270, 90);
    path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
    path.AddArc(x, y + h - d, d, d, 90, 90);
    path.CloseFigure();
    return path;
}

static byte[] ToPng(Bitmap bitmap)
{
    using var stream = new MemoryStream();
    bitmap.Save(stream, ImageFormat.Png);
    return stream.ToArray();
}

static void WriteIco(string path, List<(int Size, byte[] Png)> frames)
{
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)frames.Count);
    var offset = 6 + 16 * frames.Count;
    foreach (var frame in frames)
    {
        writer.Write((byte)(frame.Size >= 256 ? 0 : frame.Size));
        writer.Write((byte)(frame.Size >= 256 ? 0 : frame.Size));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(frame.Png.Length);
        writer.Write(offset);
        offset += frame.Png.Length;
    }

    foreach (var frame in frames)
        writer.Write(frame.Png);

    File.WriteAllBytes(path, stream.ToArray());
}

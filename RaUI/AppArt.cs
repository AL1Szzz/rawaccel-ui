using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RaUI;

/// <summary>
/// The one piece of artwork this app owns: a curve that stays flat and then rises.
/// The tray icon, the window/taskbar icon and the .ico that gets embedded into the
/// exe are all rendered from here, so the glyph can never disagree with itself and
/// there is no binary image asset to keep in sync.
///
/// The art is authored in a 32x32 unit space and scaled, which keeps the tray glyph
/// and the 256 px taskbar version the same drawing rather than two hand-tuned ones.
/// </summary>
public static class AppArt
{
    public const double UnitSpace = 32;

    /// <summary>Standard set: the sizes Explorer, the taskbar and high-DPI windows ask for.</summary>
    public static readonly int[] IcoSizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    public static void Draw(DrawingContext dc, double unit = UnitSpace)
    {
        double s = unit / UnitSpace;
        dc.PushTransform(new ScaleTransform(s, s));
        try
        {
            var bg = new LinearGradientBrush(Color.FromRgb(0x2A, 0x2F, 0x38), Color.FromRgb(0x14, 0x17, 0x1C),
                                             new Point(0, 0), new Point(UnitSpace, UnitSpace));
            bg.Freeze();
            dc.DrawRoundedRectangle(bg, null, new Rect(1, 1, UnitSpace - 2, UnitSpace - 2), 7, 7);

            // the thing this app is about: a curve that stays flat, then rises
            var line = new SolidColorBrush(Color.FromRgb(0x7C, 0xD6, 0xFF));
            line.Freeze();
            var pen = new Pen(line, 2.4)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            var fig = new PathFigure { StartPoint = new Point(5, 24) };
            fig.Segments.Add(new BezierSegment(new Point(12, 24), new Point(18, 23), new Point(21, 15), true));
            fig.Segments.Add(new BezierSegment(new Point(23, 9), new Point(25, 6), new Point(27, 5), true));
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);

            var dot = new SolidColorBrush(Color.FromRgb(0xFF, 0xC2, 0x4D));
            dot.Freeze();
            dc.DrawEllipse(dot, null, new Point(27, 6), 2.6, 2.6);
        }
        finally { dc.Pop(); }
    }

    public static RenderTargetBitmap RenderBitmap(int size)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) Draw(dc, size);
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return rtb;
    }

    public static byte[] PngBytes(int size)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(RenderBitmap(size)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>A frozen image, safe to hand to Window.Icon and to any thread.</summary>
    public static ImageSource RenderImage(int size)
    {
        var bmp = new BitmapImage();
        using (var ms = new MemoryStream(PngBytes(size)))
        {
            bmp.BeginInit();
            bmp.StreamSource = ms;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
        }
        bmp.Freeze();
        return bmp;
    }

    public static void ExportPng(string path, int size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllBytes(path, PngBytes(size));
    }

    /// <summary>
    /// Write a real .ico from PNG frames. VISTA+ icon directories accept PNG payloads, and one
    /// file carrying 16..256 is what makes the exe, the taskbar button and a 125% scaled desktop
    /// show the same art instead of a resampled blur.
    /// </summary>
    public static void ExportIco(string path, IEnumerable<int> sizes = null)
    {
        var picked = new List<int>();
        foreach (int size in sizes ?? IcoSizes)
            if (size >= 16 && size <= 256 && !picked.Contains(size)) picked.Add(size);
        if (picked.Count == 0) throw new ArgumentException("no icon frames to write");

        var frames = new List<byte[]>(picked.Count);
        foreach (int size in picked) frames.Add(PngBytes(size));

        using var w = new BinaryWriter(File.Create(path));
        w.Write((ushort)0);                 // reserved
        w.Write((ushort)1);                 // type: icon
        w.Write((ushort)frames.Count);      // count

        int offset = 6 + 16 * frames.Count;
        for (int i = 0; i < frames.Count; i++)
        {
            WriteEntry(w, picked[i], frames[i].Length, offset);
            offset += frames[i].Length;
        }
        foreach (var f in frames) w.Write(f);
    }

    static void WriteEntry(BinaryWriter w, int size, int length, int offset)
    {
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);                   // colour count (0 = truecolour)
        w.Write((byte)0);                   // reserved
        w.Write((ushort)1);                 // planes
        w.Write((ushort)32);                // bit count
        w.Write(length);
        w.Write(offset);
    }
}

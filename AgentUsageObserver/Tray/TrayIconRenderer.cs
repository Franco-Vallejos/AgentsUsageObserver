using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using AgentUsageObserver.Models;

namespace AgentUsageObserver.Tray;

/// <summary>
/// Renders a neutral tray icon with a generic usage meter behind the percentage.
/// </summary>
public static class TrayIconRenderer
{
    private const int Size = 32;

    private static readonly Color Green = Color.FromArgb(63, 185, 80);
    private static readonly Color Yellow = Color.FromArgb(227, 179, 65);
    private static readonly Color Red = Color.FromArgb(240, 90, 84);
    private static readonly Color Gray = Color.FromArgb(150, 156, 163);
    private static readonly Color GlyphGray = Color.FromArgb(208, 142, 148, 158);

    public static Icon Render(UsageSnapshot? snapshot)
    {
        using var bmp = new Bitmap(Size, Size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            var (numberColor, text) = Describe(snapshot);

            DrawUsageHalo(g, GlyphGray);
            DrawNumber(g, text, numberColor);
        }

        IntPtr hIcon = bmp.GetHicon();
        using var temp = Icon.FromHandle(hIcon);
        var managed = (Icon)temp.Clone();
        DestroyIcon(hIcon);
        return managed;
    }

    private static (Color numberColor, string text) Describe(UsageSnapshot? snapshot)
    {
        if (snapshot is null)
            return (Gray, "...");

        if (snapshot.Status == UsageStatus.NotAuthenticated)
            return (Gray, "?");

        var fiveHour = snapshot.FiveHour;
        if (fiveHour is null)
            return (Gray, snapshot.Status == UsageStatus.Error ? "!" : "?");

        int percent = (int)Math.Round(fiveHour.Percent);
        Color color = fiveHour.Severity switch
        {
            UsageSeverity.Critical => Red,
            UsageSeverity.Warning => Yellow,
            UsageSeverity.Normal => Green,
            _ => Gray
        };

        return (color, percent.ToString());
    }

    private static void DrawUsageHalo(Graphics g, Color color)
    {
        using var outline = RoundedRect(new RectangleF(5.5f, 5.5f, 21f, 21f), 6f);
        using var borderPen = new Pen(Color.FromArgb(80, color), 1.4f) { Alignment = PenAlignment.Center };
        g.DrawPath(borderPen, outline);

        using var outerPen = new Pen(color, 3.2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var innerPen = new Pen(Color.FromArgb(182, color), 2.2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        g.DrawArc(outerPen, 4.5f, 4.5f, 23f, 23f, 210, 290);
        g.DrawArc(innerPen, 8.2f, 8.2f, 15.6f, 15.6f, 18, 210);

        using var dotBrush = new SolidBrush(color);
        g.FillEllipse(dotBrush, 21.8f, 6.3f, 4.1f, 4.1f);
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        float diameter = radius * 2;
        var path = new GraphicsPath();

        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }

    private static void DrawNumber(Graphics g, string text, Color color)
    {
        float fontSize = text.Length >= 3 ? 14f : 18f;
        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        var rect = new RectangleF(0, 0, Size, Size);
        using var halo = new SolidBrush(Color.FromArgb(230, 18, 20, 24));
        foreach (var (offsetX, offsetY) in new[]
        {
            (-1.4f, 0f), (1.4f, 0f), (0f, -1.4f), (0f, 1.4f),
            (-1f, -1f), (1f, -1f), (-1f, 1f), (1f, 1f)
        })
        {
            g.DrawString(text, font, halo, new RectangleF(offsetX, offsetY, Size, Size), format);
        }

        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, rect, format);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

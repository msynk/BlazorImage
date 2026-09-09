using System.Numerics;
using BlazorImage.Drawing;
using BlazorImage.Text;
using Xunit;

namespace BlazorImage.Tests;

/// <summary>
/// Exercises the TrueType parser and text layout against a small purpose-built font
/// (see Assets/generate-test-font.py). Using a generated font rather than a system one keeps these deterministic on
/// every machine, and its glyphs are simple shapes whose coverage can be asserted exactly.
/// </summary>
public class FontTests
{
    private const string FontPath = "Assets/BlazorImageTest.ttf";

    private static FontFile LoadFont() => FontFile.Load(File.ReadAllBytes(FontPath));

    [Fact]
    public void ParsesFontHeaderAndNames()
    {
        var font = LoadFont();
        Assert.Equal(1000, font.UnitsPerEm);
        Assert.Equal(800, font.Ascender);
        Assert.Equal(-200, font.Descender);
        Assert.Equal(4, font.GlyphCount);
        Assert.Equal("BlazorImage Test", font.FamilyName);
        Assert.Equal("BlazorImage Test Regular", font.FullName);
    }

    [Fact]
    public void MapsCharactersToGlyphsThroughCmap()
    {
        var font = LoadFont();
        Assert.Equal(1, font.GetGlyphIndex('A'));
        Assert.Equal(2, font.GetGlyphIndex('B'));
        Assert.Equal(3, font.GetGlyphIndex('C'));
        Assert.True(font.HasGlyph('A'));
        // Unmapped characters fall back to .notdef rather than throwing.
        Assert.Equal(0, font.GetGlyphIndex('Z'));
        Assert.False(font.HasGlyph('Z'));
        Assert.Equal(0, font.GetGlyphIndex(0x1F600));
    }

    [Fact]
    public void ReadsHorizontalMetrics()
    {
        var font = LoadFont();
        Assert.Equal(800, font.GetAdvanceWidth(1));
        Assert.Equal(800, font.GetAdvanceWidth(2));
    }

    [Fact]
    public void ReadsKerningPairs()
    {
        var font = LoadFont();
        Assert.True(font.HasKerning);
        Assert.Equal(-50, font.GetKerning(1, 2));
        Assert.Equal(0, font.GetKerning(2, 1));
        Assert.Equal(0, font.GetKerning(3, 3));
    }

    [Fact]
    public void ExtractsGlyphOutlines()
    {
        var font = LoadFont();
        var square = font.GetGlyphOutline(1);
        Assert.NotNull(square);
        Assert.Single(square!.Contours);
        Assert.Equal(4, square.Contours[0].Points.Count);

        var ring = font.GetGlyphOutline(3);
        Assert.NotNull(ring);
        Assert.Equal(2, ring!.Contours.Count);

        // .notdef is empty in this font.
        Assert.Null(font.GetGlyphOutline(0));
    }

    [Fact]
    public void RejectsDataThatIsNotAFont()
    {
        Assert.Throws<ImageDecodeException>(() => FontFile.Load(new byte[64]));
        Assert.Throws<ImageDecodeException>(() => FontFile.Load(new byte[4]));
        // A plausible header with nothing behind it must fail cleanly rather than reading out of bounds.
        var fake = new byte[64];
        fake[1] = 1;
        Assert.Throws<ImageDecodeException>(() => FontFile.Load(fake));
    }

    [Fact]
    public void HandlesTruncatedFontsWithoutCrashing()
    {
        var full = File.ReadAllBytes(FontPath);
        for (var length = 12; length < full.Length; length += 37)
        {
            try
            {
                var font = FontFile.Load(full[..length]);
                // If it parsed, using it must still be safe.
                _ = font.GetGlyphOutline(1);
                _ = font.GetGlyphIndex('A');
            }
            catch (ImageDecodeException)
            {
                // Rejecting truncated data is the expected outcome.
            }
        }
    }
}

public class FontTextRasterizerTests
{
    private static FontTextRasterizer CreateRasterizer()
    {
        var font = FontFile.Load(File.ReadAllBytes("Assets/BlazorImageTest.ttf"));
        return new FontTextRasterizer().Register("Test", font);
    }

    private static TextStyle Style(float size = 100f) => new()
    {
        FontFamily = "Test",
        FontSize = size,
        FillColor = Rgba32.Black,
    };

    [Fact]
    public void MeasuresTextWidthFromAdvances()
    {
        var rasterizer = CreateRasterizer();
        // Advance is 800/1000 em, so at 100px each glyph advances 80px. "AB" also has -50 units of kerning (-5px).
        var metrics = rasterizer.Measure("AB", Style());
        Assert.Equal(155f, metrics.Width, 1);
        Assert.Equal(80f, metrics.Ascent, 1);
        Assert.Equal(20f, metrics.Descent, 1);
        Assert.Equal(1, metrics.LineCount);
    }

    [Fact]
    public void MeasuresEmptyTextAsZero()
    {
        var metrics = CreateRasterizer().Measure(string.Empty, Style());
        Assert.Equal(0, metrics.Width);
        Assert.Equal(0, metrics.LineCount);
    }

    [Fact]
    public void CountsExplicitLineBreaks()
    {
        var metrics = CreateRasterizer().Measure("A\nB\nC", Style());
        Assert.Equal(3, metrics.LineCount);
    }

    [Fact]
    public void WrapsAtTheMaximumWidth()
    {
        var rasterizer = CreateRasterizer();
        // Six glyphs at 80px each need 480px; a 200px limit must break them across lines.
        var metrics = rasterizer.Measure("A A A A A A", Style() with { MaxWidth = 200 });
        Assert.True(metrics.LineCount >= 3, $"Expected wrapping, got {metrics.LineCount} line(s)");
        Assert.True(metrics.Width <= 200 + 1, $"Wrapped width {metrics.Width} exceeded the limit");
    }

    [Fact]
    public void BuildsAPathWithTheRightNumberOfContours()
    {
        var rasterizer = CreateRasterizer();
        // 'A' is one square, 'C' is a square with a hole, so three contours in total.
        var path = rasterizer.GetTextPath("AC", Vector2.Zero, Style());
        Assert.NotNull(path);
        Assert.Equal(3, path!.Contours.Count);
    }

    [Fact]
    public void RendersGlyphsAsSolidShapes()
    {
        var rasterizer = CreateRasterizer();
        using var canvas = ImageCanvas.Create(200, 200, Rgba32.White);
        canvas.DrawText("A", new Vector2(20, 150), Style(), rasterizer);

        // The square glyph spans x 100..700 and y 0..700 in font units, which at 100px is x 30..90 below the baseline.
        Assert.Equal(Rgba32.Black, canvas.Buffer[60, 120]);
        // Outside the glyph the canvas is untouched.
        Assert.Equal(Rgba32.White, canvas.Buffer[150, 40]);
    }

    [Fact]
    public void HonoursAlignment()
    {
        var rasterizer = CreateRasterizer();
        using var left = ImageCanvas.Create(300, 200, Rgba32.White);
        left.DrawText("A", new Vector2(150, 150), Style() with { Alignment = TextAlignment.Left }, rasterizer);
        using var right = ImageCanvas.Create(300, 200, Rgba32.White);
        right.DrawText("A", new Vector2(150, 150), Style() with { Alignment = TextAlignment.Right }, rasterizer);

        static int FirstInkColumn(ImageCanvas c)
        {
            for (var x = 0; x < c.Width; x++)
                for (var y = 0; y < c.Height; y++)
                    if (c.Buffer[x, y].R < 128) return x;
            return -1;
        }

        var leftInk = FirstInkColumn(left);
        var rightInk = FirstInkColumn(right);
        Assert.True(leftInk > rightInk, $"Right aligned text should start further left: left={leftInk} right={rightInk}");
    }

    [Fact]
    public void HonoursBaselineMode()
    {
        var rasterizer = CreateRasterizer();
        static int FirstInkRow(ImageCanvas c)
        {
            for (var y = 0; y < c.Height; y++)
                for (var x = 0; x < c.Width; x++)
                    if (c.Buffer[x, y].R < 128) return y;
            return -1;
        }

        using var alphabetic = ImageCanvas.Create(200, 300, Rgba32.White);
        alphabetic.DrawText("A", new Vector2(20, 150), Style(), rasterizer);
        using var top = ImageCanvas.Create(200, 300, Rgba32.White);
        top.DrawText("A", new Vector2(20, 150), Style() with { Baseline = TextBaseline.Top }, rasterizer);

        // With a Top baseline the glyph sits below the given y; with an alphabetic baseline it sits above it.
        Assert.True(FirstInkRow(top) > FirstInkRow(alphabetic));
    }

    [Fact]
    public void ScalingAStyleScalesTheRenderedSize()
    {
        var rasterizer = CreateRasterizer();
        var small = rasterizer.Measure("ABC", Style(50));
        var large = rasterizer.Measure("ABC", Style(100));
        Assert.Equal(large.Width, small.Width * 2, 1);
        Assert.Equal(large.Width, rasterizer.Measure("ABC", Style(50).ForScale(2)).Width, 1);
    }

    [Fact]
    public void ReportsAClearErrorWhenNoFontIsRegistered()
    {
        var rasterizer = new FontTextRasterizer();
        Assert.False(rasterizer.HasFonts);
        var ex = Assert.Throws<ImageCapabilityException>(() => rasterizer.GetTextPath("A", Vector2.Zero, Style()));
        Assert.Contains("font", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FallsBackWhenTheRequestedFamilyIsUnknown()
    {
        var rasterizer = CreateRasterizer();
        // A CSS style family list resolves to the registered font, and an unknown family falls back rather than failing.
        Assert.NotNull(rasterizer.ResolveFont(Style() with { FontFamily = "Missing, Test, sans-serif" }));
        Assert.NotNull(rasterizer.ResolveFont(Style() with { FontFamily = "Nothing At All" }));
    }

    [Fact]
    public void LetterSpacingWidensTheLine()
    {
        var rasterizer = CreateRasterizer();
        var normal = rasterizer.Measure("AAA", Style());
        var spaced = rasterizer.Measure("AAA", Style() with { LetterSpacing = 10 });
        Assert.Equal(normal.Width + 20, spaced.Width, 1);
    }
}

using System.Drawing;
using BlazorImage.Drawing;
using BlazorImage.Operations;
using BlazorImage.Operations.Adjustments;
using BlazorImage.Operations.Filters;
using Xunit;

namespace BlazorImage.Tests;

/// <summary>
/// Filter and adjustment tests. Region-limited filters are the sharp edge here: they are what redaction is built on,
/// so their behaviour at and beyond the image boundary has to be exact and consistent across every filter.
/// </summary>
public class AdversarialFilterTests
{
    private static ImageBuffer Noise(int w, int h, int seed = 1) => AdversarialGeometryTests.Noise(w, h, seed);

    public static TheoryData<string> RegionFilters() =>
    [
        "blur", "sharpen", "pixelate", "noise", "median", "convolution",
    ];

    private static RegionOperation CreateRegionFilter(string kind, Rectangle? region) => kind switch
    {
        "blur" => new BlurFilter(3f, region),
        "sharpen" => new SharpenFilter(0.8f, 2f, 0f, region),
        "pixelate" => new PixelateFilter(4, region),
        "noise" => new NoiseFilter(0.3f, false, 7, region),
        "median" => new MedianFilter(2, region),
        "convolution" => ConvolutionFilter.EdgeDetect(region),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// A region that lies entirely outside the image must be a no-op. Every region filter has to agree: an editor
    /// that redacts a rectangle the user dragged off-canvas must not fail on one filter and succeed on another.
    /// </summary>
    [Theory]
    [MemberData(nameof(RegionFilters))]
    public void RegionEntirelyOutsideTheImageIsANoOp(string kind)
    {
        foreach (var region in new[]
                 {
                     new Rectangle(1000, 1000, 20, 20),
                     new Rectangle(-100, -100, 20, 20),
                     new Rectangle(64, 0, 10, 10),
                     new Rectangle(0, 48, 10, 10),
                 })
        {
            using var src = Noise(64, 48, 3);
            var expected = src.ToArray();
            var filter = CreateRegionFilter(kind, region);

            ImageBuffer? result = null;
            var ex = Record.Exception(() => result = filter.Apply(src, OperationContext.Default));
            using (result)
            {
                Assert.True(ex is null, $"{kind} with region {region} threw {ex?.GetType().Name}: {ex?.Message}");
                Assert.Equal(src.Size, result!.Size);
                Assert.True(expected.AsSpan().SequenceEqual(result.Bytes),
                    $"{kind} with the off-image region {region} changed pixels");
            }
        }
    }

    /// <summary>A region filter must not touch a single pixel outside its region.</summary>
    [Theory]
    [MemberData(nameof(RegionFilters))]
    public void RegionFiltersLeaveTheRestOfTheImageUntouched(string kind)
    {
        var region = new Rectangle(16, 12, 24, 20);
        using var src = Noise(64, 48, 5);
        var before = src.Clone();
        using (before)
        {
            using var result = CreateRegionFilter(kind, region).Apply(src, OperationContext.Default);
            for (var y = 0; y < src.Height; y++)
            {
                for (var x = 0; x < src.Width; x++)
                {
                    if (region.Contains(x, y)) continue;
                    Assert.True(before[x, y] == result[x, y],
                        $"{kind} changed pixel ({x},{y}) which lies outside region {region}");
                }
            }
        }
    }

    /// <summary>A region clipped by the image edge must behave as the intersection, not throw or run away.</summary>
    [Theory]
    [MemberData(nameof(RegionFilters))]
    public void PartiallyOffImageRegionsAreClipped(string kind)
    {
        foreach (var region in new[]
                 {
                     new Rectangle(-10, -10, 30, 30),
                     new Rectangle(50, 40, 40, 40),
                     new Rectangle(-5, 20, 200, 5),
                 })
        {
            using var src = Noise(64, 48, 7);
            var filter = CreateRegionFilter(kind, region);
            ImageBuffer? result = null;
            var ex = Record.Exception(() => result = filter.Apply(src, OperationContext.Default));
            using (result)
            {
                Assert.True(ex is null, $"{kind} with region {region} threw {ex?.GetType().Name}: {ex?.Message}");
                Assert.Equal(src.Size, result!.Size);
            }
        }
    }

    /// <summary>The in-place path must produce the same pixels as the allocating path.</summary>
    [Theory]
    [MemberData(nameof(RegionFilters))]
    public void InPlaceFilteringMatchesTheAllocatingPath(string kind)
    {
        foreach (Rectangle? region in new Rectangle?[] { null, new Rectangle(8, 8, 24, 16) })
        {
            using var a = Noise(48, 36, 11);
            using var b = a.Clone();
            using var expected = CreateRegionFilter(kind, region).Apply(a, OperationContext.Default);
            var mutated = CreateRegionFilter(kind, region).Apply(b, OperationContext.Default.WithMutation(true));
            Assert.True(expected.Bytes.SequenceEqual(mutated.Bytes),
                $"{kind} (region {region?.ToString() ?? "full"}) differs between the in-place and allocating paths");
        }
    }

    /// <summary>Filters must never modify the source when in-place mutation has not been granted.</summary>
    [Theory]
    [MemberData(nameof(RegionFilters))]
    public void FiltersDoNotModifyTheSourceWithoutPermission(string kind)
    {
        using var src = Noise(48, 36, 13);
        var before = src.ToArray();
        using var result = CreateRegionFilter(kind, null).Apply(src, OperationContext.Default);
        Assert.True(before.AsSpan().SequenceEqual(src.Bytes), $"{kind} modified its source buffer");
    }

    /// <summary>Filters must survive images too small for their own kernels.</summary>
    [Theory]
    [MemberData(nameof(RegionFilters))]
    public void FiltersHandleTinyImages(string kind)
    {
        foreach (var (w, h) in new[] { (1, 1), (1, 8), (8, 1), (2, 2), (3, 3) })
        {
            using var src = Noise(w, h, w * 7 + h);
            var filter = CreateRegionFilter(kind, null);
            ImageBuffer? result = null;
            var ex = Record.Exception(() => result = filter.Apply(src, OperationContext.Default));
            using (result)
            {
                Assert.True(ex is null, $"{kind} on a {w}x{h} image threw {ex?.GetType().Name}: {ex?.Message}");
                Assert.Equal(src.Size, result!.Size);
            }
        }
    }

    /// <summary>Extreme but legal parameters must not crash or produce nonsense.</summary>
    [Fact]
    public void ExtremeFilterParametersAreHandled()
    {
        var filters = new IImageOperation[]
        {
            new BlurFilter(0f),
            new BlurFilter(0.04f),
            new BlurFilter(200f),
            new SharpenFilter(0f, 1f),
            new SharpenFilter(10f, 0.5f, 1f),
            new PixelateFilter(1),
            new PixelateFilter(1000),
            new NoiseFilter(0f),
            new NoiseFilter(1f, true, int.MaxValue),
            new MedianFilter(1),
            new MedianFilter(5),
            new VignetteFilter(1f, 0f, 0f),
            new VignetteFilter(0f, 1f, 1f),
            new VignetteFilter(1f, 2f, 2f),
        };

        using var src = TestImages.Translucent(32, 32);
        foreach (var filter in filters)
        {
            using var result = filter.Apply(src, OperationContext.Default);
            Assert.Equal(src.Size, result.Size);
        }
    }

    [Fact]
    public void InvalidFilterParametersAreRejectedAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlurFilter(-1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlurFilter(float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SharpenFilter(-1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SharpenFilter(1f, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MedianFilter(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MedianFilter(6));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PosterizeFilter(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PosterizeFilter(257));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GammaAdjustment(0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GammaAdjustment(-1f));
    }

    /// <summary>A blur must preserve the average colour of a flat region and must not tint it.</summary>
    [Fact]
    public void BlurPreservesFlatColour()
    {
        var colour = new Rgba32(90, 140, 200);
        using var src = ImageBuffer.Create(64, 64, colour);
        using var result = new BlurFilter(8f).Apply(src, OperationContext.Default);
        foreach (var p in result.Pixels.ToArray())
        {
            Assert.True(Math.Abs(p.R - colour.R) <= 1 && Math.Abs(p.G - colour.G) <= 1 && Math.Abs(p.B - colour.B) <= 1 && p.A == 255,
                $"blur shifted a flat colour to {p}");
        }
    }

    /// <summary>Blurring must not pull colour out of fully transparent pixels: that is the classic dark halo bug.</summary>
    [Fact]
    public void BlurDoesNotBleedColourFromTransparentPixels()
    {
        using var src = ImageBuffer.Create(64, 64, Rgba32.Transparent);
        // An opaque white square on a transparent field whose RGB is black.
        for (var y = 20; y < 44; y++)
            for (var x = 20; x < 44; x++)
                src[x, y] = Rgba32.White;

        using var result = new BlurFilter(4f).Apply(src, OperationContext.Default);

        // Every partially transparent edge pixel must stay white, not darken towards the transparent black around it.
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var p = result[x, y];
                if (p.A is 0 or 255) continue;
                Assert.True(p.R > 200 && p.G > 200 && p.B > 200,
                    $"blur darkened a semi-transparent edge pixel at ({x},{y}) to {p}");
            }
        }
    }

    /// <summary>Adjustments must leave the alpha channel alone unless they exist to change it.</summary>
    [Fact]
    public void RgbAdjustmentsPreserveAlpha()
    {
        var ops = new IImageOperation[]
        {
            new BrightnessAdjustment(1f), new BrightnessAdjustment(-1f),
            new ContrastAdjustment(1f), new ContrastAdjustment(-1f),
            new ExposureAdjustment(5f), new ExposureAdjustment(-5f),
            new SaturationAdjustment(1f), new SaturationAdjustment(-1f),
            new HueAdjustment(180f), new HueAdjustment(-90f),
            new GammaAdjustment(0.05f), new GammaAdjustment(20f),
            new VibranceAdjustment(1f), new VibranceAdjustment(-1f),
            new ShadowsHighlightsAdjustment(1f, 1f),
            new LevelsAdjustment(0.9f, 0.91f, 0.1f),
            new TemperatureAdjustment(1f), new TintAdjustment(-1f),
            new GrayscaleFilter(), new SepiaFilter(), new InvertFilter(),
            new PosterizeFilter(2), new ThresholdFilter(0.5f),
        };

        using var src = TestImages.Translucent(32, 32);
        foreach (var op in ops)
        {
            using var dst = op.Apply(src, OperationContext.Default);
            for (var i = 0; i < src.PixelCount; i++)
                Assert.True(src.Pixels[i].A == dst.Pixels[i].A, $"{op.Name} changed alpha at pixel {i}");
        }
    }

    /// <summary>Opacity is the one adjustment that exists to change alpha, and it must change only alpha.</summary>
    [Fact]
    public void OpacityScalesAlphaAndLeavesColourAlone()
    {
        using var src = ImageBuffer.Create(16, 16, new Rgba32(200, 100, 50, 200));
        using var dst = new OpacityAdjustment(0.5f).Apply(src, OperationContext.Default);
        foreach (var p in dst.Pixels.ToArray())
        {
            Assert.Equal(200, p.R);
            Assert.Equal(100, p.G);
            Assert.Equal(50, p.B);
            Assert.InRange(p.A, 99, 101);
        }
    }

    /// <summary>Identity parameters must be recognised, so the optimiser can drop the work entirely.</summary>
    [Fact]
    public void NeutralParametersReportIdentity()
    {
        Assert.True(new BrightnessAdjustment(0f).IsIdentity);
        Assert.True(new ContrastAdjustment(0f).IsIdentity);
        Assert.True(new SaturationAdjustment(0f).IsIdentity);
        Assert.True(new HueAdjustment(0f).IsIdentity);
        Assert.True(new ExposureAdjustment(0f).IsIdentity);
        Assert.True(new VibranceAdjustment(0f).IsIdentity);
        Assert.True(new ShadowsHighlightsAdjustment(0f, 0f).IsIdentity);
        Assert.True(new GammaAdjustment(1f).IsIdentity);
        Assert.True(new BlurFilter(0f).IsIdentity);
        Assert.True(new NoiseFilter(0f).IsIdentity);
        Assert.True(new PixelateFilter(1).IsIdentity);
        Assert.True(new VignetteFilter(0f).IsIdentity);
        Assert.True(new GrayscaleFilter(0f).IsIdentity);
        Assert.True(new SepiaFilter(0f).IsIdentity);
        Assert.True(new InvertFilter(0f).IsIdentity);

        Assert.False(new BrightnessAdjustment(0.1f).IsIdentity);
        Assert.False(new GammaAdjustment(1.5f).IsIdentity);
        Assert.False(new BlurFilter(2f).IsIdentity);
    }

    /// <summary>Noise must be deterministic for a given seed, or previews and exports would disagree.</summary>
    [Fact]
    public void NoiseIsDeterministicForASeed()
    {
        using var src = Noise(32, 32, 17);
        using var a = new NoiseFilter(0.4f, false, 1234).Apply(src, OperationContext.Default);
        using var b = new NoiseFilter(0.4f, false, 1234).Apply(src, OperationContext.Default);
        using var c = new NoiseFilter(0.4f, false, 4321).Apply(src, OperationContext.Default);

        Assert.True(a.Bytes.SequenceEqual(b.Bytes), "the same seed produced different noise");
        Assert.False(a.Bytes.SequenceEqual(c.Bytes), "different seeds produced identical noise");
    }

    /// <summary>
    /// Regression guard for the zero-area rectangle trap. System.Drawing's Rectangle.IsEmpty asks whether all four
    /// fields are zero, not whether the rectangle covers any pixels, and Rectangle.Intersect returns a zero-width or
    /// zero-height rectangle whenever two rectangles merely touch along an edge. Any API taking a region has to treat
    /// those as covering nothing rather than indexing past the end of a buffer.
    /// </summary>
    [Fact]
    public void ZeroAreaRegionsAreTreatedAsCoveringNothing()
    {
        var degenerate = new Rectangle(64, 0, 0, 48);
        Assert.False(degenerate.IsEmpty, "the premise of this test is that such a rectangle is not Rectangle.Empty");

        using var image = ImageBuffer.Create(64, 48, Rgba32.White);
        var before = image.ToArray();

        // Filling and copying must not throw or write anything.
        image.Fill(degenerate, Rgba32.Black);
        Assert.True(before.AsSpan().SequenceEqual(image.Bytes));
        Assert.Throws<ArgumentException>(() => image.CopyRegion(degenerate).Dispose());

        // Drawing through a canvas clipped to nothing must draw nothing.
        using (var canvas = ImageCanvas.FromBuffer(image))
        {
            canvas.Clip = degenerate;
            canvas.FillRectangle(new RectangleF(0, 0, 64, 48), Rgba32.Black);
        }
        Assert.True(before.AsSpan().SequenceEqual(image.Bytes), "drawing through an empty clip changed pixels");

        // Statistics over an empty region must report a clear error, not crash.
        Assert.Throws<ArgumentException>(() => BlazorImage.Analysis.ImageStatistics.Measure(image, degenerate));
    }

    /// <summary>Scaling a region filter for a proxy must scale the region with it.</summary>
    [Theory]
    [MemberData(nameof(RegionFilters))]
    public void ScalingAFilterScalesItsRegion(string kind)
    {
        var region = new Rectangle(40, 20, 80, 60);
        var scaled = (RegionOperation)CreateRegionFilter(kind, region).ForScale(0.5);
        var r = scaled.Region!.Value;
        Assert.Equal(20, r.X);
        Assert.Equal(10, r.Y);
        Assert.Equal(40, r.Width);
        Assert.Equal(30, r.Height);
    }
}

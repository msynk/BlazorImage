using System.Drawing;
using System.Numerics;
using BlazorImage.Geometry;
using BlazorImage.Operations;
using BlazorImage.Operations.Adjustments;
using BlazorImage.Operations.Filters;
using BlazorImage.Operations.Transforms;
using Xunit;

namespace BlazorImage.Tests;

public class CropTests
{
    [Fact]
    public void ExtractsTheRequestedRegion()
    {
        using var source = TestImages.Gradient(20, 10);
        using var result = new CropOperation(5, 2, 8, 6).Apply(source, OperationContext.Default);
        Assert.Equal(8, result.Width);
        Assert.Equal(6, result.Height);
        for (var y = 0; y < 6; y++)
            for (var x = 0; x < 8; x++)
                Assert.Equal(source[x + 5, y + 2], result[x, y]);
    }

    [Fact]
    public void ClipsToImageBounds()
    {
        using var source = TestImages.Gradient(10, 10);
        var op = new CropOperation(5, 5, 100, 100);
        Assert.Equal(new Size(5, 5), op.GetOutputSize(source.Size));
        using var result = op.Apply(source, OperationContext.Default);
        Assert.Equal(new Size(5, 5), result.Size);
    }

    [Fact]
    public void ThrowsWhenRegionIsOutsideTheImage()
    {
        using var source = TestImages.Gradient(10, 10);
        Assert.Throws<ArgumentException>(() => new CropOperation(50, 50, 10, 10).Apply(source, OperationContext.Default));
    }

    [Fact]
    public void ConsecutiveCropsComposeToTheSameRegion()
    {
        using var source = TestImages.Gradient(40, 30);
        var a = new CropOperation(5, 4, 20, 20);
        var b = new CropOperation(3, 2, 10, 10);
        using var stepwise = b.Apply(a.Apply(source, OperationContext.Default), OperationContext.Default);
        using var combined = a.Then(b).Apply(source, OperationContext.Default);
        Assert.Equal(combined.Size, stepwise.Size);
        Assert.Equal(0, TestImages.MaxChannelDifference(combined, stepwise));
    }

    [Fact]
    public void ScalesForProxyPreviews()
    {
        var op = new CropOperation(10, 20, 40, 60);
        var half = (CropOperation)op.ForScale(0.5);
        Assert.Equal(new Rectangle(5, 10, 20, 30), half.Rectangle);
    }

    [Fact]
    public void RejectsEmptyRectangles()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CropOperation(0, 0, 0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CropOperation(0, 0, 10, -1));
    }
}

public class ResizeTests
{
    [Fact]
    public void ProducesTheExpectedSizeForEveryMode()
    {
        using var source = TestImages.Gradient(100, 50);
        foreach (var mode in Enum.GetValues<ResizeMode>())
        {
            var op = new ResizeOperation(60, 60, mode);
            using var result = op.Apply(source, OperationContext.Default);
            Assert.Equal(op.GetOutputSize(source.Size), result.Size);
        }
    }

    [Fact]
    public void PreservesASolidColourExactly()
    {
        using var source = ImageBuffer.Create(64, 64, new Rgba32(37, 111, 200));
        foreach (var filter in new[] { ResamplingFilter.Box, ResamplingFilter.Bilinear, ResamplingFilter.Bicubic, ResamplingFilter.Lanczos3, ResamplingFilter.NearestNeighbor })
        {
            using var result = new ResizeOperation(23, 41, ResizeMode.Stretch, filter).Apply(source, OperationContext.Default);
            var p = result[11, 20];
            Assert.True(Math.Abs(p.R - 37) <= 1 && Math.Abs(p.G - 111) <= 1 && Math.Abs(p.B - 200) <= 1, $"{filter} produced {p}");
            Assert.Equal(255, p.A);
        }
    }

    [Fact]
    public void IdentityResizeIsAPassThrough()
    {
        using var source = TestImages.Gradient(32, 32);
        using var result = new ResizeOperation(32, 32, ResizeMode.Stretch).Apply(source, OperationContext.Default);
        Assert.Equal(0, TestImages.MaxChannelDifference(source, result));
    }

    [Fact]
    public void DownscalingAveragesRatherThanDropsPixels()
    {
        // A 1px checkerboard downscaled by 2 must become mid grey, not pure black or white.
        using var source = ImageBuffer.Create(64, 64);
        for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
                source[x, y] = ((x + y) & 1) == 0 ? Rgba32.White : Rgba32.Black;
        using var result = new ResizeOperation(32, 32, ResizeMode.Stretch, ResamplingFilter.Box).Apply(source, OperationContext.Default);
        var p = result[16, 16];
        Assert.InRange(p.R, 100, 155);
    }

    [Fact]
    public void ContainFillsPaddingWithTheBackgroundColour()
    {
        using var source = ImageBuffer.Create(100, 50, Rgba32.White);
        var bg = new Rgba32(10, 20, 30);
        using var result = new ResizeOperation(100, 100, ResizeMode.Contain, ResamplingFilter.Bilinear, Anchor.Center, bg).Apply(source, OperationContext.Default);
        Assert.Equal(new Size(100, 100), result.Size);
        Assert.Equal(bg, result[50, 2]);
        Assert.Equal(Rgba32.White, result[50, 50]);
    }

    [Fact]
    public void DoesNotDarkenEdgesOfTransparentImages()
    {
        // A fully opaque red square on a transparent field must stay red after downscaling (premultiplied filtering).
        using var source = ImageBuffer.Create(64, 64, Rgba32.Transparent);
        source.Fill(new Rectangle(16, 16, 32, 32), new Rgba32(255, 0, 0, 255));
        using var result = new ResizeOperation(32, 32, ResizeMode.Stretch, ResamplingFilter.Bilinear).Apply(source, OperationContext.Default);
        var centre = result[16, 16];
        Assert.Equal(255, centre.A);
        Assert.True(centre.R > 250 && centre.G < 5 && centre.B < 5, $"Centre was {centre}");
        // A pixel on the boundary should be semi-transparent red, not semi-transparent black.
        var edge = result[8, 16];
        if (edge.A is > 0 and < 255) Assert.True(edge.R > 200, $"Edge pixel {edge} lost colour");
    }

    [Fact]
    public void RejectsInvalidArguments()
    {
        Assert.Throws<ArgumentException>(() => new ResizeOperation(null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResizeOperation(0, 10));
    }
}

public class RotateTests
{
    [Fact]
    public void RightAnglesUseTheLosslessPath()
    {
        using var source = TestImages.Quadrants(16, 16);
        using var viaRotate = new RotateOperation(90).Apply(source, OperationContext.Default);
        using var viaOrientation = OrientationOperation.Rotate90.Apply(source, OperationContext.Default);
        Assert.Equal(0, TestImages.MaxChannelDifference(viaRotate, viaOrientation));
    }

    [Fact]
    public void ArbitraryAngleExpandsTheCanvas()
    {
        using var source = ImageBuffer.Create(100, 100, Rgba32.White);
        var op = new RotateOperation(45, Rgba32.Transparent);
        using var result = op.Apply(source, OperationContext.Default);
        Assert.True(result.Width > 130 && result.Width < 150, $"Width was {result.Width}");
        Assert.Equal(op.GetOutputSize(source.Size), result.Size);
        Assert.Equal(0, result[0, 0].A); // corner is outside the rotated square
        Assert.Equal(255, result[result.Width / 2, result.Height / 2].A);
    }

    [Fact]
    public void FourNinetyDegreeRotationsRestoreTheOriginal()
    {
        using var source = TestImages.Gradient(23, 17);
        var ctx = OperationContext.Default;
        using var r1 = new RotateOperation(90).Apply(source, ctx);
        using var r2 = new RotateOperation(90).Apply(r1, ctx);
        using var r3 = new RotateOperation(90).Apply(r2, ctx);
        using var r4 = new RotateOperation(90).Apply(r3, ctx);
        Assert.Equal(source.Size, r4.Size);
        Assert.Equal(0, TestImages.MaxChannelDifference(source, r4));
    }

    [Fact]
    public void KeepsCanvasSizeWhenExpansionIsDisabled()
    {
        using var source = ImageBuffer.Create(50, 30, Rgba32.White);
        using var result = new RotateOperation(30, Rgba32.Transparent, expandCanvas: false).Apply(source, OperationContext.Default);
        Assert.Equal(new Size(50, 30), result.Size);
    }
}

public class PerspectiveTests
{
    [Fact]
    public void IdentityCornersReproduceTheSourceApproximately()
    {
        using var source = TestImages.Gradient(32, 32);
        var corners = new[] { new PointF(0, 0), new PointF(32, 0), new PointF(32, 32), new PointF(0, 32) };
        using var result = new PerspectiveTransformOperation(corners, new Size(32, 32)).Apply(source, OperationContext.Default);
        Assert.Equal(source.Size, result.Size);
        Assert.True(TestImages.MeanChannelDifference(source, result) < 2.0);
    }

    [Fact]
    public void RejectsDegenerateCorners()
    {
        using var source = TestImages.Gradient(16, 16);
        var collinear = new[] { new PointF(0, 0), new PointF(4, 0), new PointF(8, 0), new PointF(12, 0) };
        Assert.Throws<ArgumentException>(() => new PerspectiveTransformOperation(collinear, new Size(16, 16)).Apply(source, OperationContext.Default));
    }

    [Fact]
    public void RequiresExactlyFourCorners()
    {
        Assert.Throws<ArgumentException>(() => new PerspectiveTransformOperation([new PointF(0, 0)], null));
    }
}

public class AdjustmentTests
{
    [Fact]
    public void BrightnessLightensAndDarkens()
    {
        using var source = ImageBuffer.Create(4, 4, new Rgba32(100, 100, 100));
        using var lighter = new BrightnessAdjustment(0.2f).Apply(source, OperationContext.Default);
        using var darker = new BrightnessAdjustment(-0.2f).Apply(source, OperationContext.Default);
        Assert.Equal(151, lighter[0, 0].R);
        Assert.Equal(49, darker[0, 0].R);
    }

    [Fact]
    public void GrayscaleRemovesAllColour()
    {
        using var source = ImageBuffer.Create(4, 4, new Rgba32(200, 50, 10));
        using var gray = new GrayscaleFilter().Apply(source, OperationContext.Default);
        var p = gray[0, 0];
        Assert.Equal(p.R, p.G);
        Assert.Equal(p.G, p.B);
    }

    [Fact]
    public void AdjustmentsPreserveAlpha()
    {
        using var source = TestImages.Translucent(8, 8);
        foreach (PointOperation op in new PointOperation[]
        {
            new BrightnessAdjustment(0.3f), new ContrastAdjustment(0.4f), new SaturationAdjustment(-0.5f),
            new HueAdjustment(90), new GammaAdjustment(2.2f), new VibranceAdjustment(0.5f),
            new ShadowsHighlightsAdjustment(0.3f, -0.3f), new SepiaFilter(), new PosterizeFilter(4), new ThresholdFilter(),
        })
        {
            using var result = op.Apply(source, OperationContext.Default);
            for (var y = 0; y < 8; y++)
                for (var x = 0; x < 8; x++)
                    Assert.Equal(source[x, y].A, result[x, y].A);
        }
    }

    [Fact]
    public void OpacityScalesTheAlphaChannel()
    {
        using var source = ImageBuffer.Create(4, 4, new Rgba32(10, 20, 30, 200));
        using var result = new OpacityAdjustment(0.5f).Apply(source, OperationContext.Default);
        Assert.Equal(100, result[0, 0].A);
        Assert.Equal(10, result[0, 0].R);
    }

    [Fact]
    public void InvertIsItsOwnInverse()
    {
        using var source = TestImages.Gradient(16, 16);
        using var once = new InvertFilter().Apply(source, OperationContext.Default);
        using var twice = new InvertFilter().Apply(once, OperationContext.Default);
        Assert.True(TestImages.MaxChannelDifference(source, twice) <= 1);
    }

    [Fact]
    public void ZeroAmountAdjustmentsAreIdentity()
    {
        Assert.True(new BrightnessAdjustment(0).IsIdentity);
        Assert.True(new ContrastAdjustment(0).IsIdentity);
        Assert.True(new SaturationAdjustment(0).IsIdentity);
        Assert.True(new GammaAdjustment(1).IsIdentity);
        Assert.True(new VibranceAdjustment(0).IsIdentity);
        Assert.True(new GrayscaleFilter(0).IsIdentity);
    }

    [Fact]
    public void GammaRejectsNonPositiveValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GammaAdjustment(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GammaAdjustment(-1));
    }

    [Fact]
    public void PosterizeRejectsInvalidLevels()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PosterizeFilter(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PosterizeFilter(300));
    }
}

public class ColorMatrixTests
{
    [Fact]
    public void ConcatenationMatchesSequentialApplication()
    {
        var a = ColorMatrix.Saturate(0.5f);
        var b = ColorMatrix.Contrast(1.3f);
        var combined = ColorMatrix.Concat(a, b);
        var p = new Vector4(0.3f, 0.6f, 0.9f, 1f);
        var stepwise = b.Transform(a.Transform(p));
        var direct = combined.Transform(p);
        Assert.True(Vector4.Distance(stepwise, direct) < 1e-5f, $"{stepwise} vs {direct}");
    }

    [Fact]
    public void IdentityLeavesPixelsUnchanged()
    {
        var p = new Vector4(0.1f, 0.5f, 0.9f, 0.7f);
        Assert.Equal(p, ColorMatrix.Identity.Transform(p));
        Assert.True(ColorMatrix.Identity.IsIdentity);
    }

    [Fact]
    public void FusedMatricesProduceTheSamePixels()
    {
        using var source = TestImages.Gradient(16, 16);
        var op1 = new SaturationAdjustment(-0.3f);
        var op2 = new ContrastAdjustment(0.2f);
        using var stepwise = op2.Apply(op1.Apply(source, OperationContext.Default), OperationContext.Default);
        var fused = (PointOperation)op1.TryFuse(op2)!;
        using var combined = fused.Apply(source, OperationContext.Default);
        Assert.True(TestImages.MaxChannelDifference(stepwise, combined) <= 1);
    }

    [Fact]
    public void RoundTripsThroughArray()
    {
        var m = ColorMatrix.HueRotate(45);
        var restored = new ColorMatrix(m.ToArray());
        Assert.Equal(m, restored);
    }
}

public class FilterTests
{
    [Fact]
    public void BlurSmoothsAHardEdge()
    {
        using var source = ImageBuffer.Create(32, 32, Rgba32.Black);
        source.Fill(new Rectangle(16, 0, 16, 32), Rgba32.White);
        using var result = new BlurFilter(3f).Apply(source, OperationContext.Default);
        var atEdge = result[16, 16];
        Assert.InRange(atEdge.R, 60, 200); // no longer pure black or white
    }

    [Fact]
    public void BlurPreservesASolidColour()
    {
        using var source = ImageBuffer.Create(32, 32, new Rgba32(77, 88, 99));
        using var result = new BlurFilter(5f).Apply(source, OperationContext.Default);
        Assert.True(TestImages.MaxChannelDifference(source, result) <= 1);
    }

    [Fact]
    public void BlurOnlyAffectsItsRegion()
    {
        using var source = TestImages.Gradient(64, 64);
        var region = new Rectangle(8, 8, 16, 16);
        using var result = new BlurFilter(4f, region).Apply(source, OperationContext.Default);
        Assert.Equal(source[40, 40], result[40, 40]);
        Assert.Equal(source[0, 0], result[0, 0]);
    }

    [Fact]
    public void PixelateProducesUniformBlocks()
    {
        using var source = TestImages.Gradient(32, 32);
        using var result = new PixelateFilter(8).Apply(source, OperationContext.Default);
        var reference = result[0, 0];
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                Assert.Equal(reference, result[x, y]);
        Assert.NotEqual(reference, result[8, 0]);
    }

    [Fact]
    public void SharpenIncreasesLocalContrast()
    {
        using var source = ImageBuffer.Create(32, 32, new Rgba32(128, 128, 128));
        source.Fill(new Rectangle(16, 0, 16, 32), new Rgba32(160, 160, 160));
        using var blurred = new BlurFilter(2f).Apply(source, OperationContext.Default);
        using var sharpened = new SharpenFilter(1.0f, 2f).Apply(blurred, OperationContext.Default);
        var before = Math.Abs(blurred[15, 16].R - blurred[17, 16].R);
        var after = Math.Abs(sharpened[15, 16].R - sharpened[17, 16].R);
        Assert.True(after >= before, $"before={before} after={after}");
    }

    [Fact]
    public void NoiseIsDeterministicForASeed()
    {
        using var source = TestImages.Gradient(16, 16);
        using var a = new NoiseFilter(0.2f, seed: 42).Apply(source, OperationContext.Default);
        using var b = new NoiseFilter(0.2f, seed: 42).Apply(source, OperationContext.Default);
        Assert.Equal(0, TestImages.MaxChannelDifference(a, b));
        using var c = new NoiseFilter(0.2f, seed: 7).Apply(source, OperationContext.Default);
        Assert.True(TestImages.MaxChannelDifference(a, c) > 0);
    }

    [Fact]
    public void MedianRemovesIsolatedOutliers()
    {
        using var source = ImageBuffer.Create(16, 16, new Rgba32(100, 100, 100));
        source[8, 8] = new Rgba32(255, 0, 0);
        using var result = new MedianFilter(1).Apply(source, OperationContext.Default);
        Assert.Equal(new Rgba32(100, 100, 100), result[8, 8]);
    }

    [Fact]
    public void VignetteDarkensCornersNotTheCentre()
    {
        using var source = ImageBuffer.Create(64, 64, Rgba32.White);
        using var result = new VignetteFilter(1f, 0.5f, 0.5f).Apply(source, OperationContext.Default);
        Assert.Equal(255, result[32, 32].R);
        Assert.True(result[0, 0].R < 128, $"Corner was {result[0, 0]}");
    }

    [Fact]
    public void ConvolutionKernelMustBeOddSized()
    {
        Assert.Throws<ArgumentException>(() => new ConvolutionFilter([1, 1, 1, 1], 2, 2));
        Assert.Throws<ArgumentException>(() => new ConvolutionFilter([1, 1], 3, 3));
    }

    [Fact]
    public void RegionOperationsScaleTheirRegion()
    {
        var op = new PixelateFilter(10, new Rectangle(20, 20, 40, 40));
        var scaled = (PixelateFilter)op.ForScale(0.5);
        Assert.Equal(5, scaled.BlockSize);
        Assert.Equal(new Rectangle(10, 10, 20, 20), scaled.Region);
    }
}

public class OperationCancellationTests
{
    [Fact]
    public void ResizeHonoursCancellation()
    {
        using var source = TestImages.Gradient(1024, 1024);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ctx = new OperationContext(cancellationToken: cts.Token);
        Assert.Throws<OperationCanceledException>(() => new ResizeOperation(512, 512, ResizeMode.Stretch).Apply(source, ctx));
    }

    [Fact]
    public void ProgressReachesOneOnCompletion()
    {
        using var source = TestImages.Gradient(64, 64);
        var values = new List<double>();
        var ctx = new OperationContext(progress: new Progress<double>(v => { lock (values) values.Add(v); }));
        using var result = new ResizeOperation(32, 32, ResizeMode.Stretch).Apply(source, ctx);
        Assert.Equal(new Size(32, 32), result.Size);
    }
}

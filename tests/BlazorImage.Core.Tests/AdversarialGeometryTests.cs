using System.Drawing;
using System.Numerics;
using BlazorImage.Geometry;
using BlazorImage.Operations;
using BlazorImage.Operations.Transforms;
using Xunit;

namespace BlazorImage.Tests;

/// <summary>
/// Differential and edge-case tests for geometry, orientation and resampling. Each test states an invariant that must
/// hold in a correct implementation; a failure is a defect rather than a tightened assertion.
/// </summary>
public class AdversarialGeometryTests
{
    internal static ImageBuffer Noise(int w, int h, int seed = 1)
    {
        var rnd = new Random(seed);
        var img = ImageBuffer.Create(w, h);
        var px = img.Pixels;
        for (var i = 0; i < px.Length; i++)
            px[i] = new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), 255);
        return img;
    }

    /// <summary>The affine matrix an orientation reports must agree with the pixels it actually produces.</summary>
    [Theory]
    [InlineData(Orientation.Normal)]
    [InlineData(Orientation.FlipHorizontal)]
    [InlineData(Orientation.Rotate180)]
    [InlineData(Orientation.FlipVertical)]
    [InlineData(Orientation.Transpose)]
    [InlineData(Orientation.Rotate90)]
    [InlineData(Orientation.Transverse)]
    [InlineData(Orientation.Rotate270)]
    public void OrientationMatrixAgreesWithPixels(Orientation o)
    {
        using var src = Noise(7, 5, 3);
        using var dst = new OrientationOperation(o).Apply(src, OperationContext.Default);
        var m = o.ToMatrix(src.Size);
        for (var y = 0; y < src.Height; y++)
        {
            for (var x = 0; x < src.Width; x++)
            {
                var p = Vector2.Transform(new Vector2(x + 0.5f, y + 0.5f), m);
                var dx = (int)MathF.Floor(p.X);
                var dy = (int)MathF.Floor(p.Y);
                Assert.InRange(dx, 0, dst.Width - 1);
                Assert.InRange(dy, 0, dst.Height - 1);
                Assert.Equal(src[x, y], dst[dx, dy]);
            }
        }
    }

    /// <summary>Composing two orientations must equal applying them one after the other.</summary>
    [Fact]
    public void OrientationCompositionMatchesSequentialApplication()
    {
        var all = Enum.GetValues<Orientation>().Where(o => o != Orientation.Unspecified).ToArray();
        foreach (var a in all)
        {
            foreach (var b in all)
            {
                using var src = Noise(6, 4, 7);
                using var step1 = new OrientationOperation(a).Apply(src, OperationContext.Default);
                using var step2 = new OrientationOperation(b).Apply(step1, OperationContext.Default);
                using var combined = new OrientationOperation(a.Then(b)).Apply(src, OperationContext.Default);
                Assert.Equal(step2.Size, combined.Size);
                Assert.True(step2.Bytes.SequenceEqual(combined.Bytes), $"{a} then {b} != {a.Then(b)}");
            }
        }
    }

    [Fact]
    public void OrientationInverseUndoesOrientation()
    {
        foreach (var o in Enum.GetValues<Orientation>().Where(o => o != Orientation.Unspecified))
        {
            using var src = Noise(6, 4, 11);
            using var fwd = new OrientationOperation(o).Apply(src, OperationContext.Default);
            using var back = new OrientationOperation(o.Inverse()).Apply(fwd, OperationContext.Default);
            Assert.Equal(src.Size, back.Size);
            Assert.True(src.Bytes.SequenceEqual(back.Bytes), $"{o} then inverse is not the identity");
        }
    }

    /// <summary>The in-place mirror path must produce the same pixels as the allocating path.</summary>
    [Theory]
    [InlineData(Orientation.FlipHorizontal)]
    [InlineData(Orientation.FlipVertical)]
    [InlineData(Orientation.Rotate180)]
    public void InPlaceOrientationMatchesAllocatingPath(Orientation o)
    {
        foreach (var (w, h) in new[] { (7, 5), (8, 8), (1, 9), (9, 1), (2, 3), (5000, 2) })
        {
            using var a = Noise(w, h, 13);
            using var b = a.Clone();
            using var expected = new OrientationOperation(o).Apply(a, OperationContext.Default);
            var mutated = new OrientationOperation(o).Apply(b, OperationContext.Default.WithMutation(true));
            Assert.True(expected.Bytes.SequenceEqual(mutated.Bytes), $"{o} in-place differs from allocating at {w}x{h}");
        }
    }

    /// <summary>The box-reduce fast path must not shift or stretch the image relative to the direct path.</summary>
    [Theory]
    [InlineData(1001, 1001, 5, 5)]
    [InlineData(4032, 101, 400, 101)]
    [InlineData(999, 37, 60, 37)]
    [InlineData(1000, 1000, 7, 7)]
    [InlineData(1023, 1023, 8, 8)]
    public void PrescalePathAgreesWithDirectPath(int sw, int sh, int dw, int dh)
    {
        // A hard vertical edge exactly in the middle: any geometric shift or stretch moves the edge.
        using var src = ImageBuffer.Create(sw, sh, Rgba32.Black);
        for (var y = 0; y < sh; y++)
        {
            var row = src.GetRow(y);
            for (var x = sw / 2; x < sw; x++) row[x] = Rgba32.White;
        }

        using var viaPrescale = new ResizeOperation(dw, dh, ResizeMode.Stretch, ResamplingFilter.Lanczos3)
            .Apply(src, OperationContext.Default);
        using var direct = ResampleWithoutPrescale(src, dw, dh);

        var mean = TestImages.MeanChannelDifference(viaPrescale, direct);
        Assert.True(mean < 10, $"prescale vs direct mean difference {mean:0.0} for {sw}x{sh} -> {dw}x{dh}");
    }

    /// <summary>Repeated halving never reaches the 4x ratio that triggers the box-reduce path.</summary>
    private static ImageBuffer ResampleWithoutPrescale(ImageBuffer src, int dw, int dh)
    {
        var cur = src.Clone();
        while (cur.Width > dw * 2 || cur.Height > dh * 2)
        {
            var nw = Math.Max(dw, cur.Width / 2);
            var nh = Math.Max(dh, cur.Height / 2);
            var next = new ResizeOperation(nw, nh, ResizeMode.Stretch, ResamplingFilter.Lanczos3).Apply(cur, OperationContext.Default);
            cur.Dispose();
            cur = next;
        }
        var final = new ResizeOperation(dw, dh, ResizeMode.Stretch, ResamplingFilter.Lanczos3).Apply(cur, OperationContext.Default);
        cur.Dispose();
        return final;
    }

    /// <summary>Downscaling a solid colour must preserve that colour whichever resampling path is taken.</summary>
    [Theory]
    [InlineData(1001, 1001, 5, 5)]
    [InlineData(4000, 3000, 40, 30)]
    [InlineData(4001, 3001, 41, 31)]
    [InlineData(100, 100, 33, 33)]
    public void DownscalingSolidColourIsExact(int sw, int sh, int dw, int dh)
    {
        var colour = new Rgba32(37, 129, 211);
        using var src = ImageBuffer.Create(sw, sh, colour);
        using var dst = new ResizeOperation(dw, dh, ResizeMode.Stretch, ResamplingFilter.Auto).Apply(src, OperationContext.Default);
        foreach (var p in dst.Pixels.ToArray())
        {
            Assert.True(Math.Abs(p.R - colour.R) <= 1 && Math.Abs(p.G - colour.G) <= 1 && Math.Abs(p.B - colour.B) <= 1 && p.A == 255,
                $"solid colour not preserved: got {p}, expected {colour}, for {sw}x{sh} -> {dw}x{dh}");
        }
    }

    /// <summary>Resizing to the same size must not shift or blur the image.</summary>
    [Theory]
    [InlineData(ResamplingFilter.Bilinear)]
    [InlineData(ResamplingFilter.Bicubic)]
    [InlineData(ResamplingFilter.Lanczos3)]
    [InlineData(ResamplingFilter.Box)]
    public void IdentityResizeDoesNotShift(ResamplingFilter filter)
    {
        using var src = Noise(33, 21, 5);
        using var dst = new ResizeOperation(33, 21, ResizeMode.Stretch, filter).Apply(src, OperationContext.Default);
        var max = TestImages.MaxChannelDifference(src, dst);
        Assert.True(max <= 1, $"{filter} identity resize changed pixels by {max}");
    }

    [Fact]
    public void CropEntirelyOutsideImageThrowsClearly()
    {
        using var src = Noise(10, 10);
        Assert.Throws<ArgumentException>(() => new CropOperation(50, 50, 10, 10).Apply(src, OperationContext.Default));
    }

    [Fact]
    public void CropLargerThanImageIsClippedNotPadded()
    {
        using var src = Noise(10, 10);
        using var dst = new CropOperation(-5, -5, 100, 100).Apply(src, OperationContext.Default);
        Assert.Equal(new Size(10, 10), dst.Size);
        Assert.True(src.Bytes.SequenceEqual(dst.Bytes));
    }

    [Theory]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0, 0, 1, 0)]
    [InlineData(0, 0, -1, 5)]
    public void CropWithNonPositiveSizeThrows(int x, int y, int w, int h)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new CropOperation(x, y, w, h));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 500)]
    [InlineData(500, 1)]
    [InlineData(2, 2)]
    public void ExtremeAspectResizeWorks(int w, int h)
    {
        using var src = Noise(64, 48, 29);
        using var dst = new ResizeOperation(w, h, ResizeMode.Stretch, ResamplingFilter.Auto).Apply(src, OperationContext.Default);
        Assert.Equal(new Size(w, h), dst.Size);
    }

    /// <summary>Every resize mode must honour its documented contract for every source/target combination.</summary>
    [Theory]
    [InlineData(ResizeMode.Fit)]
    [InlineData(ResizeMode.Contain)]
    [InlineData(ResizeMode.Cover)]
    [InlineData(ResizeMode.Max)]
    [InlineData(ResizeMode.Min)]
    [InlineData(ResizeMode.Stretch)]
    public void ResizeModesHonourTheirContract(ResizeMode mode)
    {
        foreach (var (sw, sh) in new[] { (1, 1), (1, 1000), (1000, 1), (37, 91), (640, 480) })
        {
            foreach (var (tw, th) in new[] { (1, 1), (16, 16), (2000, 3), (300, 300) })
            {
                using var src = ImageBuffer.Create(sw, sh, new Rgba32(10, 20, 30));
                var op = new ResizeOperation(tw, th, mode);
                var predicted = op.GetOutputSize(src.Size);
                // Min enlarges until both dimensions reach the target, which for an extreme aspect ratio can demand
                // an enormous canvas. GetOutputSize must say so up front so a caller can check before allocating.
                if (!ImageLimits.Default.Allows(predicted.Width, predicted.Height))
                {
                    Assert.Throws<ImageLimitExceededException>(() => op.Apply(src, OperationContext.Default).Dispose());
                    continue;
                }
                using var dst = op.Apply(src, OperationContext.Default);
                Assert.True(dst.Width > 0 && dst.Height > 0);
                Assert.Equal(predicted, dst.Size);
                if (mode is ResizeMode.Fit or ResizeMode.Max)
                    Assert.True(dst.Width <= tw && dst.Height <= th, $"{mode} {sw}x{sh} -> {tw}x{th} produced {dst.Size}");
                if (mode is ResizeMode.Contain or ResizeMode.Cover or ResizeMode.Stretch)
                    Assert.Equal(new Size(tw, th), dst.Size);
                if (mode is ResizeMode.Max)
                    Assert.True(dst.Width <= sw && dst.Height <= sh, $"Max enlarged {sw}x{sh} to {dst.Size}");
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    [InlineData(360)]
    [InlineData(-90)]
    [InlineData(45)]
    [InlineData(13.37)]
    [InlineData(359.99)]
    public void RotateProducesPredictedSize(double degrees)
    {
        using var src = Noise(40, 24, 31);
        var op = new RotateOperation(degrees);
        var predicted = op.GetOutputSize(src.Size);
        using var dst = op.Apply(src, OperationContext.Default);
        Assert.Equal(predicted, dst.Size);
    }

    [Fact]
    public void FourNinetyDegreeRotationsRestoreTheOriginal()
    {
        using var src = Noise(17, 11, 37);
        var cur = src.Clone();
        for (var i = 0; i < 4; i++)
        {
            var next = new RotateOperation(90).Apply(cur, OperationContext.Default);
            cur.Dispose();
            cur = next;
        }
        Assert.Equal(src.Size, cur.Size);
        Assert.True(src.Bytes.SequenceEqual(cur.Bytes));
        cur.Dispose();
    }

    /// <summary>An arbitrary rotation must not lose the image: rotating a filled frame keeps its centre opaque.</summary>
    [Theory]
    [InlineData(45)]
    [InlineData(13.37)]
    [InlineData(200.5)]
    [InlineData(359.99)]
    public void ArbitraryRotationKeepsContent(double degrees)
    {
        using var src = ImageBuffer.Create(64, 48, new Rgba32(200, 100, 50));
        using var dst = new RotateOperation(degrees).Apply(src, OperationContext.Default);
        var centre = dst[dst.Width / 2, dst.Height / 2];
        Assert.Equal(255, centre.A);
        Assert.InRange(centre.R, 190, 210);
    }

    /// <summary>
    /// The affine fast path exists only for speed, so it must agree with the general delegate path. The two compute
    /// the same source position with a different order of floating point operations, so a pixel that lands exactly on
    /// a rounding boundary can differ by one unit; anything beyond that, or more than a handful of such pixels, would
    /// mean the fast path is sampling somewhere else.
    /// </summary>
    [Theory]
    [InlineData(WarpSampling.NearestNeighbor)]
    [InlineData(WarpSampling.Bilinear)]
    [InlineData(WarpSampling.Bicubic)]
    public void AffineWarpFastPathMatchesTheGeneralPath(WarpSampling sampling)
    {
        foreach (var degrees in new[] { 13.37, 37, 45, 200.5, 359.99 })
        {
            using var src = Noise(61, 43, 97);
            var radians = (float)(degrees * Math.PI / 180);
            var forward = Matrix3x2.CreateRotation(radians, new Vector2(src.Width / 2f, src.Height / 2f));
            Assert.True(Matrix3x2.Invert(forward, out var inverse));

            using var viaFastPath = ImageBuffer.Create(src.Width, src.Height, clear: false);
            using var viaGeneralPath = ImageBuffer.Create(src.Width, src.Height, clear: false);
            var background = new Rgba32(10, 20, 30, 200);

            Warp.RunAffine(src, viaFastPath, inverse, sampling, background, OperationContext.Default);
            Warp.Run(src, viaGeneralPath, p => Vector2.Transform(p, inverse), sampling, background, OperationContext.Default);

            var maxDifference = TestImages.MaxChannelDifference(viaFastPath, viaGeneralPath);
            Assert.True(maxDifference <= 1,
                $"{sampling} at {degrees} degrees differs by {maxDifference} between the affine and general warp paths");

            // A one-unit tolerance must not be able to hide a shifted image, so also require that almost every pixel
            // is bit-identical: a genuine geometric error would move a large fraction of them.
            var identical = 0;
            var a = viaFastPath.Pixels;
            var b = viaGeneralPath.Pixels;
            for (var i = 0; i < a.Length; i++) if (a[i] == b[i]) identical++;
            var fraction = identical / (double)a.Length;
            Assert.True(fraction > 0.97,
                $"{sampling} at {degrees} degrees: only {fraction:P1} of pixels matched exactly between the warp paths");
        }
    }

    [Fact]
    public void SkewWithDegenerateAnglesFailsAtConstructionNotUse()
    {
        // A 90 degree skew is not an invertible transform; it must be rejected up front rather than
        // throwing from GetOutputSize or Apply later on.
        Assert.ThrowsAny<ArgumentException>(() => new SkewOperation(90, 90));
    }

    [Fact]
    public void AllocationBeyondLimitsThrowsBeforeAllocating()
        => Assert.Throws<ImageLimitExceededException>(() => ImageBuffer.Create(100000, 100000));

    [Fact]
    public void ResizeBeyondLimitsThrowsInsteadOfExhaustingMemory()
    {
        using var src = Noise(16, 16);
        Assert.Throws<ImageLimitExceededException>(
            () => new ResizeOperation(30000, 30000, ResizeMode.Stretch).Apply(src, OperationContext.Default));
    }

    [Fact]
    public void RotationBeyondLimitsThrowsInsteadOfExhaustingMemory()
    {
        using var src = ImageBuffer.Create(16000, 3000, Rgba32.White);
        // The expanded canvas of a 45 degree rotation exceeds the default 16384px limit.
        Assert.Throws<ImageLimitExceededException>(() => new RotateOperation(45).Apply(src, OperationContext.Default));
    }
}

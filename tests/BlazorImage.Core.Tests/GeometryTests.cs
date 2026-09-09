using System.Drawing;
using BlazorImage.Geometry;
using Xunit;

namespace BlazorImage.Tests;

public class OrientationTests
{
    [Fact]
    public void EveryOrientationComposedWithItsInverseIsNormal()
    {
        foreach (Orientation o in Enum.GetValues<Orientation>())
        {
            if (o == Orientation.Unspecified) continue;
            Assert.Equal(Orientation.Normal, o.Then(o.Inverse()));
            Assert.Equal(Orientation.Normal, o.Inverse().Then(o));
        }
    }

    [Fact]
    public void CompositionMatchesPixelTransformForAllPairs()
    {
        // The algebraic composition must agree with actually applying both transforms to pixels.
        var values = Enum.GetValues<Orientation>().Where(o => o != Orientation.Unspecified).ToArray();
        foreach (var a in values)
        {
            foreach (var b in values)
            {
                using var source = TestImages.Gradient(7, 5);
                using var stepwise = Apply(Apply(source, a), b);
                using var combined = Apply(source, a.Then(b));
                Assert.Equal(combined.Width, stepwise.Width);
                Assert.Equal(combined.Height, stepwise.Height);
                Assert.Equal(0, TestImages.MaxChannelDifference(combined, stepwise));
            }
        }

        static ImageBuffer Apply(ImageBuffer src, Orientation o)
            => new Operations.Transforms.OrientationOperation(o).Apply(src, Operations.OperationContext.Default);
    }

    [Fact]
    public void MatrixAgreesWithPixelMapping()
    {
        var size = new Size(7, 5);
        foreach (var o in Enum.GetValues<Orientation>().Where(v => v != Orientation.Unspecified))
        {
            using var source = TestImages.Gradient(size.Width, size.Height);
            using var rotated = new Operations.Transforms.OrientationOperation(o).Apply(source, Operations.OperationContext.Default);
            for (var y = 0; y < size.Height; y++)
            {
                for (var x = 0; x < size.Width; x++)
                {
                    // Map the pixel centre and check the destination pixel matches.
                    var p = o.Transform(new PointF(x + 0.5f, y + 0.5f), size);
                    var dx = (int)Math.Floor(p.X);
                    var dy = (int)Math.Floor(p.Y);
                    Assert.InRange(dx, 0, rotated.Width - 1);
                    Assert.InRange(dy, 0, rotated.Height - 1);
                    Assert.Equal(source[x, y], rotated[dx, dy]);
                }
            }
        }
    }

    [Theory]
    [InlineData(Orientation.Rotate90, true)]
    [InlineData(Orientation.Rotate270, true)]
    [InlineData(Orientation.Transpose, true)]
    [InlineData(Orientation.Transverse, true)]
    [InlineData(Orientation.Normal, false)]
    [InlineData(Orientation.Rotate180, false)]
    [InlineData(Orientation.FlipHorizontal, false)]
    public void ReportsDimensionSwaps(Orientation o, bool swaps) => Assert.Equal(swaps, o.SwapsDimensions());

    [Fact]
    public void FromRotationRoundTrips()
    {
        Assert.Equal(Orientation.Rotate90, OrientationMath.FromRotation(90));
        Assert.Equal(Orientation.Rotate180, OrientationMath.FromRotation(180));
        Assert.Equal(Orientation.Rotate270, OrientationMath.FromRotation(-90));
        Assert.Equal(Orientation.Normal, OrientationMath.FromRotation(360));
    }
}

public class ResizePlanTests
{
    private static readonly Size Landscape = new(1000, 500);

    [Fact]
    public void FitKeepsAspectRatioInsideBounds()
    {
        var plan = ResizePlan.Compute(Landscape, 400, 400, ResizeMode.Fit);
        Assert.Equal(new Size(400, 200), plan.CanvasSize);
    }

    [Fact]
    public void ContainPadsToExactTarget()
    {
        var plan = ResizePlan.Compute(Landscape, 400, 400, ResizeMode.Contain);
        Assert.Equal(new Size(400, 400), plan.CanvasSize);
        Assert.Equal(new Rectangle(0, 100, 400, 200), plan.DestinationRect);
    }

    [Fact]
    public void CoverCropsSourceToExactTarget()
    {
        var plan = ResizePlan.Compute(Landscape, 400, 400, ResizeMode.Cover);
        Assert.Equal(new Size(400, 400), plan.CanvasSize);
        Assert.Equal(new Rectangle(0, 0, 400, 400), plan.DestinationRect);
        Assert.Equal(500, plan.SourceRect.Width);
        Assert.Equal(500, plan.SourceRect.Height);
        Assert.Equal(250, plan.SourceRect.X);
    }

    [Fact]
    public void MaxNeverEnlarges()
    {
        var plan = ResizePlan.Compute(new Size(100, 50), 400, 400, ResizeMode.Max);
        Assert.Equal(new Size(100, 50), plan.CanvasSize);
    }

    [Fact]
    public void MinNeverShrinks()
    {
        var plan = ResizePlan.Compute(new Size(100, 50), 20, 20, ResizeMode.Min);
        Assert.Equal(new Size(100, 50), plan.CanvasSize);
    }

    [Fact]
    public void SingleDimensionDerivesTheOtherProportionally()
    {
        var plan = ResizePlan.Compute(Landscape, 250, null);
        Assert.Equal(new Size(250, 125), plan.CanvasSize);
        plan = ResizePlan.Compute(Landscape, null, 125);
        Assert.Equal(new Size(250, 125), plan.CanvasSize);
    }

    [Fact]
    public void StretchIgnoresAspectRatio()
    {
        var plan = ResizePlan.Compute(Landscape, 300, 300, ResizeMode.Stretch);
        Assert.Equal(new Size(300, 300), plan.CanvasSize);
    }

    [Fact]
    public void CoverRespectsAnchor()
    {
        var top = ResizePlan.Compute(Landscape, 400, 400, ResizeMode.Cover, Anchor.Left);
        Assert.Equal(0, top.SourceRect.X);
        var right = ResizePlan.Compute(Landscape, 400, 400, ResizeMode.Cover, Anchor.Right);
        Assert.Equal(500, right.SourceRect.X);
    }

    [Fact]
    public void RejectsInvalidArguments()
    {
        Assert.Throws<ArgumentException>(() => ResizePlan.Compute(Landscape, null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => ResizePlan.Compute(Landscape, 0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => ResizePlan.Compute(new Size(0, 10), 10, 10));
    }
}

public class AspectRatioTests
{
    [Fact]
    public void FitsLargestRectangleInsideBounds()
    {
        var r = AspectRatio.Ratio16x9.FitInside(new Rectangle(0, 0, 100, 100));
        Assert.Equal(100, r.Width);
        Assert.Equal(56, r.Height);
        Assert.Equal(22, r.Y); // centred
    }

    [Fact]
    public void SquareInsideWideBoundsIsLimitedByHeight()
    {
        var r = AspectRatio.Square.FitInside(new Rectangle(0, 0, 200, 50));
        Assert.Equal(new Size(50, 50), r.Size);
        Assert.Equal(75, r.X);
    }

    [Fact]
    public void AnchorPositionsTheResult()
    {
        var r = AspectRatio.Square.FitInside(new Rectangle(0, 0, 200, 50), Anchor.TopLeft);
        Assert.Equal(new Point(0, 0), r.Location);
    }
}

public class AnchorTests
{
    [Theory]
    [InlineData(Anchor.TopLeft, 0, 0)]
    [InlineData(Anchor.TopRight, 8, 0)]
    [InlineData(Anchor.BottomLeft, 0, 6)]
    [InlineData(Anchor.BottomRight, 8, 6)]
    [InlineData(Anchor.Center, 4, 3)]
    [InlineData(Anchor.Top, 4, 0)]
    [InlineData(Anchor.Left, 0, 3)]
    public void PlacesContentInsideArea(Anchor anchor, int expectedX, int expectedY)
    {
        var r = anchor.Place(new Size(2, 4), new Size(10, 10));
        Assert.Equal(new Point(expectedX, expectedY), r.Location);
    }
}

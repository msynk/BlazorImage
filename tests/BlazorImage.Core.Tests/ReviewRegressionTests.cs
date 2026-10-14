using System.Buffers.Binary;
using System.Drawing;
using System.Numerics;
using BlazorImage.Analysis;
using BlazorImage.Annotations;
using BlazorImage.Codecs;
using BlazorImage.Codecs.Bmp;
using BlazorImage.Codecs.Jpeg;
using BlazorImage.Drawing;
using BlazorImage.Editing;
using BlazorImage.Geometry;
using BlazorImage.Metadata;
using BlazorImage.Operations;
using BlazorImage.Operations.Adjustments;
using BlazorImage.Operations.Filters;
using BlazorImage.Operations.Transforms;
using BlazorImage.Pipeline;
using BlazorImage.Text;
using Xunit;

namespace BlazorImage.Tests;

/// <summary>Regression tests for defects found in a code review, one per defect.</summary>
public class ReviewRegressionTests
{
    // ---------------------------------------------------------------- drawing

    [Fact]
    public void RoundJoinsLeaveNoHoleWhereTheyOverlapOneSegment()
    {
        using var canvas = ImageCanvas.Create(100, 100);
        canvas.DrawPolyline([new Vector2(10, 50), new Vector2(50, 50), new Vector2(50, 90)], Rgba32.Black,
            new StrokeStyle { Width = 20, Join = LineJoin.Round });
        // Inside the second segment and the join circle, but not the first segment.
        Assert.Equal(255, canvas.Buffer[52, 52].A);
    }

    [Fact]
    public void AnOpenPolylineThatReturnsToItsStartDrawsItsLastSegment()
    {
        using var canvas = ImageCanvas.Create(100, 100);
        canvas.DrawPolyline([new Vector2(10, 10), new Vector2(90, 10), new Vector2(90, 90), new Vector2(10, 10)], Rgba32.Black,
            new StrokeStyle { Width = 4 });
        Assert.True(canvas.Buffer[50, 50].A > 0, "the diagonal back to the start was not drawn");
    }

    [Fact]
    public void ANegativeDashOffsetShiftsThePattern()
    {
        static ImageBuffer Draw(float offset)
        {
            var canvas = ImageCanvas.Create(100, 10);
            canvas.DrawLine(new Vector2(0, 5), new Vector2(100, 5), Rgba32.Black,
                new StrokeStyle { Width = 4, Cap = LineCap.Butt, DashPattern = [10f, 10f], DashOffset = offset });
            return canvas.Snapshot();
        }
        using var zero = Draw(0);
        using var shifted = Draw(-5);
        Assert.False(zero.Bytes.SequenceEqual(shifted.Bytes));
    }

    [Fact]
    public void NonFiniteCoordinatesDoNotCrashOrBlankTheRestOfThePath()
    {
        using var canvas = ImageCanvas.Create(100, 100);
        var path = new PathBuilder()
            .AddRectangle(new RectangleF(10, 10, 50, 50))
            .AddPolygon([new Vector2(0, 0), new Vector2(float.NaN, 20), new Vector2(30, float.NaN)])
            .Build();
        canvas.FillPath(path, Rgba32.Black);
        Assert.Equal(255, canvas.Buffer[30, 30].A);
    }

    [Fact]
    public void HugeCoordinatesStillPaintTheVisiblePart()
    {
        using var canvas = ImageCanvas.Create(100, 100);
        canvas.FillPolygon([new Vector2(0, 0), new Vector2(100, 0), new Vector2(100, 3e9f), new Vector2(0, 3e9f)], Rgba32.Black);
        Assert.Equal(255, canvas.Buffer[50, 50].A);
    }

    // ---------------------------------------------------------------- annotations

    [Fact]
    public void RotatedLinesAreHitWhereTheyAreDrawn()
    {
        var line = new LineAnnotation { Start = new Vector2(0, 50), End = new Vector2(100, 50), Rotation = 90 };
        Assert.True(line.HitTest(new Vector2(50, 5)));
        Assert.False(line.HitTest(new Vector2(10, 50)));
    }

    [Fact]
    public void RotatedPixelateRedactionCoversTheRotatedRegion()
    {
        using var image = TestImages.Gradient(200, 200);
        using var original = image.Clone();
        var redaction = new RedactionAnnotation
        {
            Rectangle = new RectangleF(50, 90, 100, 20),
            Mode = RedactionMode.Pixelate,
            Strength = 8,
            Rotation = 90,
        };
        using var canvas = ImageCanvas.FromBuffer(image);
        redaction.RenderWithTransform(canvas, new AnnotationRenderContext());

        // Rotated a quarter turn the strip runs vertically through the centre: the top of it must have changed, and a
        // point on the unrotated strip's far end, now outside it, must not.
        Assert.NotEqual(original[103, 55], image[103, 55]);
        Assert.Equal(original[55, 100], image[55, 100]);
    }

    // ---------------------------------------------------------------- pipeline

    [Fact]
    public void MergingACropWithANegativeOriginKeepsTheOutputSize()
    {
        using var source = TestImages.Gradient(200, 200);
        var pipeline = ImagePipeline.Create().Crop(-10, -10, 100, 100).Crop(0, 0, 50, 50);
        using var plain = pipeline.Execute(source, new PipelineExecutionOptions { Optimize = false });
        using var optimised = pipeline.Execute(source, new PipelineExecutionOptions { Optimize = true });
        Assert.Equal(plain.Size, optimised.Size);
        Assert.True(plain.Bytes.SequenceEqual(optimised.Bytes));
    }

    [Fact]
    public void ResizeCropFusionIsNotPlannedPastAutoOrient()
    {
        using var source = TestImages.Gradient(400, 300);
        source.Metadata = new ImageMetadata(ExifData.FromOrientation(Orientation.Rotate90));
        var pipeline = ImagePipeline.Create().AutoOrient().Resize(100, 100, ResizeMode.Fit).Crop(0, 0, 50, 100);
        using var plain = pipeline.Execute(source, new PipelineExecutionOptions { Optimize = false });
        using var optimised = pipeline.Execute(source, new PipelineExecutionOptions { Optimize = true });
        Assert.Equal(new Size(50, 100), plain.Size);
        Assert.Equal(plain.Size, optimised.Size);
    }

    [Fact]
    public void AQuarterTurnWithoutExpandingKeepsTheCanvasSize()
    {
        using var source = TestImages.Gradient(400, 300);
        using var rotated = new RotateOperation(90, expandCanvas: false).Apply(source, OperationContext.Default);
        Assert.Equal(source.Size, rotated.Size);
    }

    [Fact]
    public void TheZerothPercentileIsTheDarkestLevelPresent()
    {
        using var image = ImageBuffer.Create(120, 1);
        for (var x = 0; x < 120; x++) image[x, 0] = new Rgba32((byte)(60 + x), (byte)(60 + x), (byte)(60 + x));
        var stats = ImageStatistics.Measure(image, maxSamples: 0);
        Assert.Equal(stats.Red.Minimum, stats.Red.GetPercentile(0));
    }

    // ---------------------------------------------------------------- history

    [Fact]
    public void UndoAfterADragReturnsToTheStateBeforeIt()
    {
        using var session = new ImageEditSession(TestImages.Gradient(64, 64));
        session.AddOperation(new CropOperation(0, 0, 32, 32), "Crop");
        var beforeDrag = session.Document;

        for (var i = 1; i <= 3; i++)
        {
            var amount = i / 10f;
            session.ApplyTransient(d => d.AddOperation(new BrightnessAdjustment(amount)));
        }
        session.Commit("Brightness");

        Assert.Equal(3, session.History.Entries.Count);
        Assert.Equal("Brightness", session.History.CurrentEntry.Label);
        session.Undo();
        Assert.Same(beforeDrag, session.Document);
    }

    [Fact]
    public void ApplyingTheFinalValueOfADragAddsNoSecondEntry()
    {
        using var session = new ImageEditSession(TestImages.Gradient(64, 64));
        session.ApplyTransient(d => d.AddOperation(new BrightnessAdjustment(0.1f)));
        session.ApplyTransient(d => d.WithPipeline(d.Pipeline.Replace(0, new BrightnessAdjustment(0.2f))));
        session.Apply(d => d.WithPipeline(d.Pipeline.Replace(0, new BrightnessAdjustment(0.2f))), "Brightness");

        Assert.Equal(2, session.History.Entries.Count);
        Assert.Equal("Brightness", session.History.CurrentEntry.Label);
    }

    [Fact]
    public void CommittingWithoutADragAddsNothing()
    {
        using var session = new ImageEditSession(TestImages.Gradient(64, 64));
        session.AddOperation(new BrightnessAdjustment(0.2f));
        session.Undo();
        session.Commit("Select");
        Assert.True(session.History.CanRedo, "a click that changed nothing discarded the redo trail");
    }

    [Fact]
    public void AHistoryDepthOfOneStillKeepsTheLatestEdit()
    {
        var original = ImageDocument.Create(new Size(10, 10));
        var history = new EditHistory(original, maxDepth: 1);
        var edited = original.AddOperation(new BrightnessAdjustment(0.5f));
        history.Push(edited, "edit");
        Assert.Same(edited, history.Current);
    }

    [Fact]
    public async Task APreviewRenderedBeforeAnEditIsStillStale()
    {
        using var session = new ImageEditSession(TestImages.Gradient(64, 64));
        var render = session.RenderPreviewAsync();
        session.AddOperation(new BrightnessAdjustment(0.4f));
        await render;
        Assert.True(session.IsPreviewStale);
    }

    [Fact]
    public async Task DisposingDuringARenderDoesNotFaultIt()
    {
        var session = new ImageEditSession(TestImages.Gradient(512, 512));
        session.AddOperation(new BlurFilter(8));
        var render = session.RenderPreviewAsync().AsTask();
        session.Dispose();
        var error = await Record.ExceptionAsync(() => render);
        Assert.True(error is null or OperationCanceledException, $"unexpected {error?.GetType().Name}: {error?.Message}");
    }

    // ---------------------------------------------------------------- codecs

    [Fact]
    public void JpegDecodesNeutralGreyWithoutAGreenCast()
    {
        using var source = ImageBuffer.Create(32, 32, new Rgba32(100, 100, 100));
        var encoded = JpegEncoder.Encode(source, new ImageExportOptions { Format = ImageFormat.Jpeg, Quality = 1.0 });
        using var decoded = JpegDecoder.Decode(encoded.Data);
        var pixel = decoded[16, 16];
        Assert.Equal(pixel.R, pixel.G);
        Assert.Equal(pixel.B, pixel.G);
    }

    [Fact]
    public void BmpWithTheMinimumHeightIsRejectedCleanly()
    {
        var bmp = new byte[64];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), int.MinValue);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(28), 24);
        Assert.Throws<ImageDecodeException>(() => BmpDecoder.Decode(bmp));
    }

    [Fact]
    public void BmpWithAnOverflowingHeaderSizeIsRejectedCleanly()
    {
        var bmp = new byte[64];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), int.MaxValue - 5);
        Assert.Throws<ImageDecodeException>(() => BmpDecoder.Decode(bmp));
    }

    [Fact]
    public void PngChunkLengthsThatWouldOverflowAreIgnored()
    {
        using var source = TestImages.Gradient(8, 8);
        var png = Codecs.Png.PngEncoder.Encode(source).Data.ToArray();
        // Corrupt the IHDR length to just below int.MaxValue.
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8), 0x7FFFFFF3);
        var error = Record.Exception(() => Codecs.Png.PngDecoder.Decode(png));
        Assert.True(error is null or ImageException, $"unexpected {error?.GetType().Name}");
    }

    [Fact]
    public void CloningIgnoresTheDefaultLimitsTheSourceWasAllowedPast()
    {
        using var wide = ImageBuffer.Create(40_000, 2, limits: ImageLimits.Unrestricted);
        using var copy = wide.Clone();
        Assert.Equal(wide.Size, copy.Size);
    }

    [Fact]
    public void JpegRefusesDimensionsItCannotStore()
    {
        using var wide = ImageBuffer.Create(70_000, 1, limits: ImageLimits.Unrestricted);
        Assert.Throws<ImageEncodeException>(() => JpegEncoder.Encode(wide, new ImageExportOptions { Format = ImageFormat.Jpeg }));
    }

    // ---------------------------------------------------------------- text

    [Fact]
    public void ALongWordAfterTheFirstIsStillBrokenToFit()
    {
        var font = FontFile.Load(File.ReadAllBytes("Assets/BlazorImageTest.ttf"));
        var rasterizer = new FontTextRasterizer().Register("Test", font);
        var metrics = rasterizer.Measure("AB AAAAAAAAAAAA", new TextStyle { FontFamily = "Test", FontSize = 10, MaxWidth = 40 });
        Assert.True(metrics.Width <= 40.5f, $"a line is {metrics.Width}px wide, past the 40px limit");
    }
}

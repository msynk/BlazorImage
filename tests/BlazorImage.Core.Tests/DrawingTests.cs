using System.Drawing;
using System.Numerics;
using BlazorImage.Annotations;
using BlazorImage.Drawing;
using BlazorImage.Editing;
using BlazorImage.Pipeline;
using Xunit;

namespace BlazorImage.Tests;

public class RasterizerTests
{
    [Fact]
    public void FillsARectangleExactly()
    {
        using var canvas = ImageCanvas.Create(32, 32);
        canvas.FillRectangle(new RectangleF(8, 8, 16, 16), Rgba32.Black);
        Assert.Equal(Rgba32.Black, canvas.Buffer[8, 8]);
        Assert.Equal(Rgba32.Black, canvas.Buffer[23, 23]);
        Assert.Equal(0, canvas.Buffer[7, 7].A);
        Assert.Equal(0, canvas.Buffer[24, 24].A);
    }

    [Fact]
    public void AntiAliasesDiagonalEdges()
    {
        using var canvas = ImageCanvas.Create(64, 64);
        // Right triangle whose hypotenuse is the line x + y = 64.
        var triangle = new PathBuilder().AddPolygon([new Vector2(0, 0), new Vector2(64, 0), new Vector2(0, 64)]).Build();
        canvas.FillPath(triangle, Rgba32.Black);
        for (var i = 8; i < 56; i++)
        {
            var y = 64 - i;
            // The pixel whose centre sits exactly on the hypotenuse must be half covered.
            Assert.InRange(canvas.Buffer[i - 1, y].A, 118, 138);
            // Well inside is opaque, well outside is clear.
            Assert.Equal(255, canvas.Buffer[i - 4, y].A);
            Assert.Equal(0, canvas.Buffer[i + 2, y].A);
        }
    }

    [Fact]
    public void EvenOddRuleCutsHoles()
    {
        using var canvas = ImageCanvas.Create(64, 64);
        var builder = new PathBuilder();
        builder.AddRectangle(new RectangleF(8, 8, 48, 48));
        builder.AddRectangle(new RectangleF(20, 20, 24, 24));
        canvas.FillPath(builder.Build(), Rgba32.Black, FillRule.EvenOdd);
        Assert.Equal(255, canvas.Buffer[10, 32].A);
        Assert.Equal(0, canvas.Buffer[32, 32].A);
    }

    [Fact]
    public void NonZeroRuleFillsNestedContours()
    {
        using var canvas = ImageCanvas.Create(64, 64);
        var builder = new PathBuilder();
        builder.AddRectangle(new RectangleF(8, 8, 48, 48));
        builder.AddRectangle(new RectangleF(20, 20, 24, 24));
        canvas.FillPath(builder.Build(), Rgba32.Black, FillRule.NonZero);
        Assert.Equal(255, canvas.Buffer[32, 32].A);
    }

    [Fact]
    public void RespectsTheClipRectangle()
    {
        using var canvas = ImageCanvas.Create(32, 32);
        canvas.Clip = new Rectangle(0, 0, 16, 32);
        canvas.FillRectangle(new RectangleF(0, 0, 32, 32), Rgba32.Black);
        Assert.Equal(255, canvas.Buffer[8, 8].A);
        Assert.Equal(0, canvas.Buffer[24, 8].A);
    }

    [Fact]
    public void RespectsOpacity()
    {
        using var canvas = ImageCanvas.Create(16, 16, Rgba32.White);
        canvas.Opacity = 0.5f;
        canvas.FillRectangle(new RectangleF(0, 0, 16, 16), Rgba32.Black);
        var p = canvas.Buffer[8, 8];
        Assert.InRange(p.R, 120, 135);
    }

    [Fact]
    public void FillsWithALinearGradient()
    {
        using var canvas = ImageCanvas.Create(64, 8);
        var paint = new LinearGradientPaint(new Vector2(0, 0), new Vector2(64, 0), [(0f, Rgba32.Black), (1f, Rgba32.White)]);
        canvas.FillPath(new PathBuilder().AddRectangle(new RectangleF(0, 0, 64, 8)).Build(), paint);
        Assert.True(canvas.Buffer[1, 4].R < 20);
        Assert.True(canvas.Buffer[62, 4].R > 235);
        Assert.InRange(canvas.Buffer[32, 4].R, 110, 145);
    }

    [Fact]
    public void SaveAndRestoreRoundTripState()
    {
        using var canvas = ImageCanvas.Create(16, 16);
        canvas.Save();
        canvas.Opacity = 0.25f;
        canvas.Translate(5, 5);
        canvas.Restore();
        Assert.Equal(1f, canvas.Opacity);
        Assert.True(canvas.Transform.IsIdentity);
    }
}

public class StrokerTests
{
    [Fact]
    public void StrokeWidthIsHonoured()
    {
        using var canvas = ImageCanvas.Create(64, 64);
        canvas.DrawLine(new Vector2(32, 0), new Vector2(32, 64), Rgba32.Black, new StrokeStyle { Width = 8, Cap = LineCap.Butt });
        var covered = 0;
        for (var x = 0; x < 64; x++) if (canvas.Buffer[x, 32].A > 128) covered++;
        Assert.InRange(covered, 7, 9);
    }

    [Fact]
    public void RoundCapsExtendBeyondTheEndpoints()
    {
        using var canvas = ImageCanvas.Create(64, 64);
        canvas.DrawLine(new Vector2(20, 32), new Vector2(44, 32), Rgba32.Black, new StrokeStyle { Width = 10, Cap = LineCap.Round });
        Assert.True(canvas.Buffer[17, 32].A > 100, "Round cap did not extend past the start point");
        using var butt = ImageCanvas.Create(64, 64);
        butt.DrawLine(new Vector2(20, 32), new Vector2(44, 32), Rgba32.Black, new StrokeStyle { Width = 10, Cap = LineCap.Butt });
        Assert.True(butt.Buffer[17, 32].A < 20, "Butt cap extended past the start point");
    }

    [Fact]
    public void DashedLinesLeaveGaps()
    {
        using var canvas = ImageCanvas.Create(100, 16);
        canvas.DrawLine(new Vector2(0, 8), new Vector2(100, 8), Rgba32.Black, new StrokeStyle { Width = 4, Cap = LineCap.Butt, DashPattern = [10f, 10f] });
        var covered = 0;
        var gaps = 0;
        for (var x = 0; x < 100; x++)
        {
            if (canvas.Buffer[x, 8].A > 200) covered++;
            else if (canvas.Buffer[x, 8].A < 20) gaps++;
        }
        Assert.True(covered > 20, $"Too little ink: {covered}");
        Assert.True(gaps > 20, $"No gaps in the dashed line: {gaps}");
    }

    [Fact]
    public void ClosedShapesAreStrokedAllTheWayAround()
    {
        using var canvas = ImageCanvas.Create(64, 64);
        canvas.DrawRectangle(new RectangleF(16, 16, 32, 32), Rgba32.Black, new StrokeStyle { Width = 4 });
        Assert.True(canvas.Buffer[32, 16].A > 200, "top edge missing");
        Assert.True(canvas.Buffer[32, 48].A > 200, "bottom edge missing");
        Assert.True(canvas.Buffer[16, 32].A > 200, "left edge missing");
        Assert.True(canvas.Buffer[48, 32].A > 200, "right edge missing");
        Assert.True(canvas.Buffer[32, 32].A < 20, "interior should not be filled");
    }

    [Fact]
    public void ArrowsDrawAHeadAtTheEnd()
    {
        using var canvas = ImageCanvas.Create(80, 80);
        canvas.DrawArrow(new Vector2(10, 40), new Vector2(70, 40), Rgba32.Black, new StrokeStyle { Width = 4 });
        Assert.True(canvas.Buffer[40, 40].A > 200, "shaft missing");
        // The head is wider than the shaft near the tip.
        var shaftWidth = CountColumn(canvas, 30);
        var headWidth = CountColumn(canvas, 60);
        Assert.True(headWidth > shaftWidth, $"head ({headWidth}) not wider than shaft ({shaftWidth})");

        static int CountColumn(ImageCanvas c, int x)
        {
            var n = 0;
            for (var y = 0; y < 80; y++) if (c.Buffer[x, y].A > 128) n++;
            return n;
        }
    }

    [Fact]
    public void RejectsNonPositiveStrokeWidth()
    {
        var path = new PathBuilder().MoveTo(0, 0).LineTo(10, 10).Build();
        Assert.Throws<ArgumentOutOfRangeException>(() => Stroker.CreateStrokeOutline(path, new StrokeStyle { Width = 0 }));
    }
}

public class CanvasImageTests
{
    [Fact]
    public void DrawsAnImageAtItsNaturalSize()
    {
        using var source = TestImages.Quadrants(16, 16);
        using var canvas = ImageCanvas.Create(32, 32);
        canvas.DrawImage(source, new Vector2(8, 8));
        Assert.Equal(source[0, 0], canvas.Buffer[8, 8]);
        Assert.Equal(source[15, 15], canvas.Buffer[23, 23]);
        Assert.Equal(0, canvas.Buffer[0, 0].A);
    }

    [Fact]
    public void ScalesAnImageIntoADestinationRectangle()
    {
        using var source = ImageBuffer.Create(8, 8, new Rgba32(20, 120, 220));
        using var canvas = ImageCanvas.Create(64, 64);
        canvas.DrawImage(source, new RectangleF(0, 0, 64, 64));
        var p = canvas.Buffer[32, 32];
        Assert.True(Math.Abs(p.R - 20) <= 2 && Math.Abs(p.G - 120) <= 2 && Math.Abs(p.B - 220) <= 2, $"{p}");
    }

    [Fact]
    public void ComposesTransparencyCorrectly()
    {
        using var source = ImageBuffer.Create(16, 16, new Rgba32(255, 0, 0, 128));
        using var canvas = ImageCanvas.Create(16, 16, Rgba32.White);
        canvas.DrawImage(source, Vector2.Zero);
        var p = canvas.Buffer[8, 8];
        Assert.Equal(255, p.A);
        Assert.InRange(p.R, 250, 255);
        Assert.InRange(p.G, 120, 135);
    }

    [Fact]
    public void DrawsUnderARotationTransform()
    {
        using var source = ImageBuffer.Create(20, 20, Rgba32.Black);
        using var canvas = ImageCanvas.Create(64, 64);
        canvas.Rotate(45, new Vector2(32, 32));
        canvas.DrawImage(source, new RectangleF(22, 22, 20, 20));
        Assert.True(canvas.Buffer[32, 32].A > 200, "rotated image did not cover the centre");
    }

    [Fact]
    public void EraseBlendModeRemovesAlpha()
    {
        using var canvas = ImageCanvas.Create(16, 16, Rgba32.Black);
        using var mask = ImageBuffer.Create(8, 8, Rgba32.White);
        canvas.DrawImage(mask, new Vector2(0, 0), BlendMode.Erase);
        Assert.Equal(0, canvas.Buffer[4, 4].A);
        Assert.Equal(255, canvas.Buffer[12, 12].A);
    }

    [Fact]
    public void ApplyPipelineReplacesCanvasContents()
    {
        using var canvas = ImageCanvas.Create(32, 32, new Rgba32(200, 100, 50));
        canvas.ApplyPipeline(ImagePipeline.Create().Grayscale());
        var p = canvas.Buffer[16, 16];
        Assert.Equal(p.R, p.G);
        Assert.Equal(p.G, p.B);
    }

    [Fact]
    public void ApplyPipelineRejectsResizeOnABorrowedBuffer()
    {
        using var buffer = ImageBuffer.Create(32, 32, Rgba32.White);
        using var canvas = ImageCanvas.FromBuffer(buffer);
        Assert.Throws<InvalidOperationException>(() => canvas.ApplyPipeline(ImagePipeline.Create().Resize(16, 16, Geometry.ResizeMode.Stretch)));
    }
}

public class AnnotationTests
{
    [Fact]
    public void RectangleAnnotationRendersItsOutline()
    {
        using var canvas = ImageCanvas.Create(64, 64);
        var annotation = new RectangleAnnotation { Rectangle = new RectangleF(16, 16, 32, 32) };
        annotation.Render(canvas, new AnnotationRenderContext());
        Assert.True(canvas.Buffer[32, 16].A > 200);
        Assert.True(canvas.Buffer[32, 32].A < 20);
    }

    [Fact]
    public void TranslatePreservesIdentityAndShape()
    {
        var original = new RectangleAnnotation { Rectangle = new RectangleF(10, 10, 20, 20) };
        var moved = (RectangleAnnotation)original.Translate(5, -5);
        Assert.Equal(original.Id, moved.Id);
        Assert.Equal(new RectangleF(15, 5, 20, 20), moved.Rectangle);
        Assert.Equal(new RectangleF(10, 10, 20, 20), original.Rectangle);
    }

    [Fact]
    public void HitTestingFindsShapesAndMissesEmptySpace()
    {
        var rect = new RectangleAnnotation { Rectangle = new RectangleF(10, 10, 20, 20) };
        Assert.True(rect.HitTest(new Vector2(20, 20)));
        Assert.False(rect.HitTest(new Vector2(60, 60)));

        var ellipse = new EllipseAnnotation { Rectangle = new RectangleF(0, 0, 40, 40) };
        Assert.True(ellipse.HitTest(new Vector2(20, 20)));
        Assert.False(ellipse.HitTest(new Vector2(2, 2)));

        var line = new LineAnnotation { Start = new Vector2(0, 0), End = new Vector2(40, 40) };
        Assert.True(line.HitTest(new Vector2(20, 20)));
        Assert.False(line.HitTest(new Vector2(0, 40)));
    }

    [Fact]
    public void ForScaleScalesGeometryAndStrokeWidth()
    {
        var arrow = new ArrowAnnotation
        {
            Start = new Vector2(10, 10),
            End = new Vector2(50, 50),
            Style = ShapeStyle.Default with { Stroke = new StrokeStyle { Width = 8 } },
        };
        var half = (ArrowAnnotation)arrow.ForScale(0.5);
        Assert.Equal(new Vector2(5, 5), half.Start);
        Assert.Equal(new Vector2(25, 25), half.End);
        Assert.Equal(4f, half.Style.Stroke.Width);
    }

    [Fact]
    public void RedactionActuallyDestroysTheUnderlyingPixels()
    {
        using var canvas = ImageCanvas.FromImage(TestImages.Gradient(64, 64));
        var before = canvas.Buffer[20, 20];
        var redaction = new RedactionAnnotation { Rectangle = new RectangleF(10, 10, 30, 30), Mode = RedactionMode.Solid };
        redaction.Render(canvas, new AnnotationRenderContext());
        Assert.Equal(Rgba32.Black, canvas.Buffer[20, 20]);
        Assert.NotEqual(before, canvas.Buffer[20, 20]);
        Assert.NotEqual(Rgba32.Black, canvas.Buffer[60, 60]);
    }

    [Fact]
    public void PixelateRedactionRemovesFineDetail()
    {
        using var canvas = ImageCanvas.FromImage(TestImages.Gradient(64, 64));
        var redaction = new RedactionAnnotation { Rectangle = new RectangleF(0, 0, 32, 32), Mode = RedactionMode.Pixelate, Strength = 16 };
        redaction.Render(canvas, new AnnotationRenderContext());
        Assert.Equal(canvas.Buffer[0, 0], canvas.Buffer[15, 15]);
        Assert.NotEqual(canvas.Buffer[0, 0], canvas.Buffer[40, 40]);
    }

    [Fact]
    public void HighlightBlendsRatherThanCovers()
    {
        using var canvas = ImageCanvas.Create(32, 32, Rgba32.White);
        new HighlightAnnotation { Rectangle = new RectangleF(0, 0, 32, 32) }.Render(canvas, new AnnotationRenderContext());
        var p = canvas.Buffer[16, 16];
        Assert.True(p.R > 200 && p.B < 200, $"Highlight did not tint the background: {p}");
    }

    [Fact]
    public void FreehandSmoothingProducesAContinuousStroke()
    {
        using var canvas = ImageCanvas.Create(64, 64);
        var stroke = new FreehandAnnotation
        {
            Points = [new Vector2(8, 32), new Vector2(24, 16), new Vector2(40, 48), new Vector2(56, 32)],
        };
        stroke.Render(canvas, new AnnotationRenderContext());
        // Every column between the endpoints must have some ink.
        for (var x = 12; x <= 52; x++)
        {
            var any = false;
            for (var y = 0; y < 64 && !any; y++) any = canvas.Buffer[x, y].A > 60;
            Assert.True(any, $"Gap in the freehand stroke at x={x}");
        }
    }

    [Fact]
    public void InvisibleAnnotationsAreNotDrawn()
    {
        using var canvas = ImageCanvas.Create(32, 32);
        var annotation = new RectangleAnnotation { Rectangle = new RectangleF(4, 4, 24, 24), IsVisible = false };
        annotation.RenderWithTransform(canvas, new AnnotationRenderContext());
        Assert.Equal(0, canvas.Buffer[4, 4].A);
    }

    [Fact]
    public void RotationChangesRenderedOutputAndBounds()
    {
        var annotation = new RectangleAnnotation { Rectangle = new RectangleF(10, 20, 40, 10), Rotation = 90 };
        var bounds = annotation.GetRotatedBounds();
        Assert.True(bounds.Height > bounds.Width, $"Rotated bounds were not reoriented: {bounds}");
    }
}

public class DocumentAndHistoryTests
{
    [Fact]
    public void DocumentsAreImmutable()
    {
        var doc = ImageDocument.Create(new Size(100, 100));
        var withCrop = doc.AddOperation(new Operations.Transforms.CropOperation(0, 0, 50, 50));
        Assert.True(doc.IsEmpty);
        Assert.Single(withCrop.Pipeline);
        Assert.Equal(new Size(50, 50), withCrop.OutputSize);
        Assert.Equal(new Size(100, 100), doc.OutputSize);
    }

    [Fact]
    public void AnnotationsCanBeAddedUpdatedAndRemoved()
    {
        var doc = ImageDocument.Create(new Size(100, 100));
        var arrow = new ArrowAnnotation { Start = new Vector2(0, 0), End = new Vector2(10, 10) };
        doc = doc.AddAnnotation(arrow);
        Assert.Single(doc.Annotations);

        var moved = arrow.Translate(5, 5);
        doc = doc.UpdateAnnotation(moved);
        Assert.Single(doc.Annotations);
        Assert.Equal(new Vector2(5, 5), ((ArrowAnnotation)doc.Annotations[0]).Start);

        doc = doc.RemoveAnnotation(arrow.Id);
        Assert.Empty(doc.Annotations);
    }

    [Fact]
    public void RedactionsAlwaysRenderBeforeOtherAnnotations()
    {
        var doc = ImageDocument.Create(new Size(100, 100))
            .AddAnnotation(new ArrowAnnotation { Start = Vector2.Zero, End = new Vector2(10, 10) })
            .AddAnnotation(new RedactionAnnotation { Rectangle = new RectangleF(0, 0, 20, 20) });
        var order = doc.GetRenderOrder();
        Assert.IsType<RedactionAnnotation>(order[0]);
        Assert.IsType<ArrowAnnotation>(order[1]);
    }

    [Fact]
    public void RedactionDoesNotEraseAnnotationsDrawnOnTop()
    {
        using var source = TestImages.Gradient(64, 64);
        var doc = ImageDocument.Create(source)
            .AddAnnotation(new ArrowAnnotation
            {
                Start = new Vector2(4, 32),
                End = new Vector2(60, 32),
                Style = ShapeStyle.Default with { StrokeColor = new Rgba32(255, 0, 0), Stroke = new StrokeStyle { Width = 6 } },
            })
            .AddAnnotation(new RedactionAnnotation { Rectangle = new RectangleF(0, 0, 64, 64), Mode = RedactionMode.Solid });
        using var rendered = new DocumentRenderer().Render(doc, source);
        var p = rendered[32, 32];
        Assert.True(p.R > 200 && p.G < 60, $"The arrow was redacted away: {p}");
    }

    [Fact]
    public void HitTestingPicksTheTopmostAnnotation()
    {
        var bottom = new RectangleAnnotation { Rectangle = new RectangleF(0, 0, 50, 50) };
        var top = new RectangleAnnotation { Rectangle = new RectangleF(10, 10, 20, 20) };
        var doc = ImageDocument.Create(new Size(100, 100)).AddAnnotation(bottom).AddAnnotation(top);
        Assert.Equal(top.Id, doc.HitTest(new Vector2(20, 20))!.Id);
        Assert.Equal(bottom.Id, doc.HitTest(new Vector2(45, 45))!.Id);
        Assert.Null(doc.HitTest(new Vector2(90, 90)));
    }

    [Fact]
    public void ForScaleScalesBothPipelineAndAnnotations()
    {
        var doc = ImageDocument.Create(new Size(1000, 800))
            .AddOperation(new Operations.Transforms.CropOperation(100, 100, 400, 400))
            .AddAnnotation(new ArrowAnnotation { Start = new Vector2(200, 200), End = new Vector2(400, 400) });
        var half = doc.ForScale(0.5);
        Assert.Equal(new Size(500, 400), half.SourceSize);
        Assert.Equal(new Rectangle(50, 50, 200, 200), ((Operations.Transforms.CropOperation)half.Pipeline[0]).Rectangle);
        Assert.Equal(new Vector2(100, 100), ((ArrowAnnotation)half.Annotations[0]).Start);
    }

    [Fact]
    public void HistoryUndoesAndRedoes()
    {
        var doc = ImageDocument.Create(new Size(100, 100));
        var history = new EditHistory(doc);
        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);

        history.Push(doc.AddOperation(new Operations.Adjustments.BrightnessAdjustment(0.2f)), "Brightness");
        Assert.True(history.CanUndo);
        Assert.Single(history.Current.Pipeline);

        history.Undo();
        Assert.Empty(history.Current.Pipeline);
        Assert.True(history.CanRedo);

        history.Redo();
        Assert.Single(history.Current.Pipeline);
    }

    [Fact]
    public void EditingAfterUndoDiscardsTheRedoTrail()
    {
        var history = new EditHistory(ImageDocument.Create(new Size(50, 50)));
        history.Push(history.Current.AddOperation(new Operations.Adjustments.BrightnessAdjustment(0.1f)), "A");
        history.Push(history.Current.AddOperation(new Operations.Adjustments.ContrastAdjustment(0.1f)), "B");
        history.Undo();
        Assert.True(history.CanRedo);
        history.Push(history.Current.AddOperation(new Operations.Adjustments.SaturationAdjustment(0.1f)), "C");
        Assert.False(history.CanRedo);
        Assert.Equal("C", history.CurrentEntry.Label);
    }

    [Fact]
    public void AmendReplacesTheCurrentEntryInsteadOfGrowingHistory()
    {
        var history = new EditHistory(ImageDocument.Create(new Size(50, 50)));
        history.Push(history.Current.AddOperation(new Operations.Adjustments.BrightnessAdjustment(0.1f)), "Brightness");
        var count = history.Entries.Count;
        for (var i = 0; i < 20; i++)
            history.Amend(ImageDocument.Create(new Size(50, 50)).AddOperation(new Operations.Adjustments.BrightnessAdjustment(i / 100f)));
        Assert.Equal(count, history.Entries.Count);
    }

    [Fact]
    public void HistoryRespectsItsDepthLimitAndKeepsTheOriginal()
    {
        var original = ImageDocument.Create(new Size(50, 50));
        var history = new EditHistory(original, maxDepth: 5);
        for (var i = 0; i < 20; i++)
            history.Push(history.Current.AddOperation(new Operations.Adjustments.BrightnessAdjustment(0.01f * (i + 1))), $"Step {i}");
        Assert.Equal(5, history.Entries.Count);
        Assert.True(history.Entries[0].Document.IsEmpty, "The original state was discarded");
    }

    [Fact]
    public void HistoryMemoryStaysSmallForManyEdits()
    {
        var history = new EditHistory(ImageDocument.Create(new Size(6000, 4000)), maxDepth: 100);
        for (var i = 0; i < 99; i++)
            history.Push(history.Current.AddOperation(new Operations.Adjustments.BrightnessAdjustment(0.001f * i)), $"Step {i}");
        // 100 undo steps on a 24 megapixel image must not cost anything like 100 copies of the pixels.
        Assert.True(history.EstimatedMemory < 1_000_000, $"History used {history.EstimatedMemory} bytes");
    }

    [Fact]
    public void ResetReturnsToTheOriginalWithoutLosingRedo()
    {
        var history = new EditHistory(ImageDocument.Create(new Size(50, 50)));
        history.Push(history.Current.AddOperation(new Operations.Adjustments.BrightnessAdjustment(0.5f)), "Brightness");
        history.Reset();
        Assert.True(history.Current.IsEmpty);
        Assert.True(history.CanRedo);
    }

    [Fact]
    public void RendererAppliesPipelineThenAnnotations()
    {
        using var source = ImageBuffer.Create(64, 64, Rgba32.White);
        var doc = ImageDocument.Create(source)
            .AddOperation(new Operations.Adjustments.BrightnessAdjustment(-0.5f))
            .AddAnnotation(new RectangleAnnotation
            {
                Rectangle = new RectangleF(8, 8, 20, 20),
                Style = ShapeStyle.Default with { FillColor = new Rgba32(0, 200, 0), StrokeColor = null },
            });
        using var result = new DocumentRenderer().Render(doc, source);
        Assert.InRange(result[50, 50].R, 120, 135);          // darkened background
        Assert.Equal(new Rgba32(0, 200, 0), result[16, 16]); // annotation on top, undarkened
    }
}

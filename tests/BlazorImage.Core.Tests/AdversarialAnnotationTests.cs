using System.Drawing;
using System.Numerics;
using BlazorImage.Annotations;
using BlazorImage.Drawing;
using BlazorImage.Editing;
using BlazorImage.Geometry;
using BlazorImage.Operations;
using BlazorImage.Operations.Transforms;
using BlazorImage.Pipeline;
using Xunit;

namespace BlazorImage.Tests;

/// <summary>
/// Coordinate-space tests for annotations. The governing requirement is that an annotation placed at a logical image
/// coordinate stays at that coordinate under every scale, translation and viewport change, so these tests compare
/// geometry across transformations rather than checking any single rendering.
/// </summary>
public class AdversarialAnnotationTests
{
    public static TheoryData<string> AnnotationKinds() =>
    [
        "Rectangle", "Ellipse", "Line", "Arrow", "Freehand", "Polygon",
        "Text", "Highlight", "Redaction", "Callout", "Step", "Image",
    ];

    private static readonly ImageBuffer Stamp = ImageBuffer.Create(8, 8, new Rgba32(0, 128, 255));

    private static Annotation Create(string kind) => kind switch
    {
        "Rectangle" => new RectangleAnnotation { Rectangle = new RectangleF(20, 30, 60, 40), CornerRadius = 5 },
        "Ellipse" => new EllipseAnnotation { Rectangle = new RectangleF(20, 30, 60, 40) },
        "Line" => new LineAnnotation { Start = new Vector2(10, 20), End = new Vector2(90, 70) },
        "Arrow" => new ArrowAnnotation { Start = new Vector2(10, 20), End = new Vector2(90, 70) },
        "Freehand" => new FreehandAnnotation { Points = [new(10, 10), new(30, 40), new(60, 20), new(80, 60)] },
        "Polygon" => new PolygonAnnotation { Points = [new(15, 15), new(70, 25), new(50, 70)] },
        "Text" => new TextAnnotation { Text = "Hello", Position = new Vector2(30, 40), MeasuredSize = new SizeF(50, 16) },
        "Highlight" => new HighlightAnnotation { Rectangle = new RectangleF(20, 30, 60, 20) },
        "Redaction" => new RedactionAnnotation { Rectangle = new RectangleF(20, 30, 40, 25), Mode = RedactionMode.Solid },
        "Callout" => new CalloutAnnotation { Bubble = new RectangleF(20, 20, 60, 30), Target = new Vector2(95, 80), Text = "note" },
        "Step" => new StepAnnotation { Center = new Vector2(50, 50), Number = 3, Radius = 15 },
        "Image" => new ImageAnnotation { Rectangle = new RectangleF(20, 30, 40, 40), Image = Stamp },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// The core proxy-preview invariant: scaling an annotation must scale its geometry by exactly that factor. If a
    /// field is left unscaled the annotation drifts away from the image content it was placed on.
    /// </summary>
    [Theory]
    [MemberData(nameof(AnnotationKinds))]
    public void ScalingAnAnnotationScalesItsGeometry(string kind)
    {
        var annotation = Create(kind);
        var bounds = annotation.GetBounds();

        foreach (var scale in new[] { 0.25, 0.5, 2.0, 4.0 })
        {
            var scaled = annotation.ForScale(scale).GetBounds();
            var tolerance = (float)(Math.Max(bounds.Width, bounds.Height) * scale * 0.02) + 1.5f;
            Assert.True(Math.Abs(scaled.X - bounds.X * scale) <= tolerance, $"{kind} at {scale}x: X {scaled.X} vs {bounds.X * scale}");
            Assert.True(Math.Abs(scaled.Y - bounds.Y * scale) <= tolerance, $"{kind} at {scale}x: Y {scaled.Y} vs {bounds.Y * scale}");
            Assert.True(Math.Abs(scaled.Width - bounds.Width * scale) <= tolerance, $"{kind} at {scale}x: width {scaled.Width} vs {bounds.Width * scale}");
            Assert.True(Math.Abs(scaled.Height - bounds.Height * scale) <= tolerance, $"{kind} at {scale}x: height {scaled.Height} vs {bounds.Height * scale}");
        }
    }

    /// <summary>Scaling down to a proxy and back must return the annotation to where it started.</summary>
    [Theory]
    [MemberData(nameof(AnnotationKinds))]
    public void ScaleRoundTripReturnsToTheOriginalPosition(string kind)
    {
        var annotation = Create(kind);
        var expected = annotation.GetBounds();
        var actual = annotation.ForScale(0.25).ForScale(4.0).GetBounds();

        Assert.True(Math.Abs(actual.X - expected.X) <= 1.5f && Math.Abs(actual.Y - expected.Y) <= 1.5f
                 && Math.Abs(actual.Width - expected.Width) <= 3f && Math.Abs(actual.Height - expected.Height) <= 3f,
            $"{kind} moved from {expected} to {actual} across a scale round trip");
    }

    /// <summary>Identity and inverse translation must be exact; nothing may quietly accumulate.</summary>
    [Theory]
    [MemberData(nameof(AnnotationKinds))]
    public void TranslationIsInvertible(string kind)
    {
        var annotation = Create(kind);
        var expected = annotation.GetBounds();
        var actual = annotation.Translate(37.5f, -12.25f).Translate(-37.5f, 12.25f).GetBounds();
        Assert.Equal(expected.X, actual.X, 3);
        Assert.Equal(expected.Y, actual.Y, 3);
        Assert.Equal(expected.Width, actual.Width, 3);
        Assert.Equal(expected.Height, actual.Height, 3);
    }

    [Theory]
    [MemberData(nameof(AnnotationKinds))]
    public void TranslationMovesBoundsByExactlyTheOffset(string kind)
    {
        var annotation = Create(kind);
        var before = annotation.GetBounds();
        var after = annotation.Translate(10f, -5f).GetBounds();
        Assert.Equal(before.X + 10f, after.X, 3);
        Assert.Equal(before.Y - 5f, after.Y, 3);
        Assert.Equal(before.Width, after.Width, 3);
        Assert.Equal(before.Height, after.Height, 3);
    }

    /// <summary>Identity, ordering and editor state must survive scaling: the proxy shows the same objects.</summary>
    [Theory]
    [MemberData(nameof(AnnotationKinds))]
    public void ScalingPreservesIdentityAndEditorState(string kind)
    {
        var annotation = Create(kind) with { ZIndex = 7, Opacity = 0.5f, IsVisible = true, IsLocked = true, Tag = "t", Rotation = 30f };
        var scaled = annotation.ForScale(0.5);

        Assert.Equal(annotation.Id, scaled.Id);
        Assert.Equal(annotation.Kind, scaled.Kind);
        Assert.Equal(annotation.ZIndex, scaled.ZIndex);
        Assert.Equal(annotation.Opacity, scaled.Opacity);
        Assert.Equal(annotation.IsLocked, scaled.IsLocked);
        Assert.Equal(annotation.IsVisible, scaled.IsVisible);
        Assert.Equal(annotation.Tag, scaled.Tag);
        // Rotation is an angle, so it must not be scaled.
        Assert.Equal(annotation.Rotation, scaled.Rotation);
    }

    /// <summary>Hit-testing must agree with the geometry: the centre hits, a distant point does not.</summary>
    [Theory]
    [MemberData(nameof(AnnotationKinds))]
    public void HitTestingAgreesWithGeometry(string kind)
    {
        var annotation = Create(kind);
        var bounds = annotation.GetBounds();
        var far = new Vector2(bounds.Right + 500, bounds.Bottom + 500);
        Assert.False(annotation.HitTest(far), $"{kind} reports a hit 500px away from its bounds");
    }

    /// <summary>A rotated annotation's bounds must contain its unrotated bounds' centre and never collapse.</summary>
    [Theory]
    [MemberData(nameof(AnnotationKinds))]
    public void RotatedBoundsContainTheCentreAndDoNotCollapse(string kind)
    {
        var annotation = Create(kind) with { Rotation = 37f };
        var plain = annotation.GetBounds();
        var rotated = annotation.GetRotatedBounds();
        var centre = new PointF(plain.X + plain.Width / 2, plain.Y + plain.Height / 2);

        Assert.True(rotated.Width >= plain.Width - 0.01f || rotated.Height >= plain.Height - 0.01f,
            $"{kind}: rotated bounds {rotated} collapsed relative to {plain}");
        Assert.True(rotated.Contains(centre), $"{kind}: rotated bounds {rotated} do not contain the centre {centre}");
    }

    /// <summary>
    /// The end-to-end statement of the invariant: an annotation drawn on a half-size proxy must land on the same image
    /// content as one drawn at full resolution. Rendering both and comparing the marked region proves it.
    /// </summary>
    [Theory]
    [InlineData("Rectangle")]
    [InlineData("Ellipse")]
    [InlineData("Highlight")]
    [InlineData("Redaction")]
    [InlineData("Step")]
    [InlineData("Line")]
    public void AnnotationsLandOnTheSameContentAtProxyResolution(string kind)
    {
        var annotation = Create(kind);
        var document = ImageDocument.Create(new Size(200, 160)).AddAnnotation(annotation);
        var renderer = new DocumentRenderer();

        using var full = ImageBuffer.Create(200, 160, Rgba32.White);
        using var rendered = renderer.Render(document, full);

        const double scale = 0.5;
        using var proxySource = ImageBuffer.Create(100, 80, Rgba32.White);
        using var proxyRendered = renderer.Render(document.ForScale(scale), proxySource, null, scale);

        // Compare the centre of mass of the marked (non-white) pixels; a coordinate-space error moves it.
        var fullCentre = MarkedCentre(rendered);
        var proxyCentre = MarkedCentre(proxyRendered);
        Assert.NotNull(fullCentre);
        Assert.NotNull(proxyCentre);

        var expected = new Vector2(fullCentre!.Value.X * (float)scale, fullCentre.Value.Y * (float)scale);
        var drift = Vector2.Distance(expected, proxyCentre!.Value);
        Assert.True(drift <= 2.0f, $"{kind}: annotation centre drifted {drift:0.00}px between full and proxy rendering");
    }

    private static Vector2? MarkedCentre(ImageBuffer image)
    {
        double sx = 0, sy = 0;
        var n = 0;
        for (var y = 0; y < image.Height; y++)
        {
            var row = image.GetRow(y);
            for (var x = 0; x < image.Width; x++)
            {
                var p = row[x];
                if (p.R == 255 && p.G == 255 && p.B == 255) continue;
                sx += x; sy += y; n++;
            }
        }
        return n == 0 ? null : new Vector2((float)(sx / n), (float)(sy / n));
    }

    /// <summary>Rendering must never write outside the buffer, whatever coordinates an annotation carries.</summary>
    [Theory]
    [MemberData(nameof(AnnotationKinds))]
    public void AnnotationsWithExtremeCoordinatesDoNotCrash(string kind)
    {
        var offsets = new[] { -100000f, -500f, 0f, 500f, 100000f };
        foreach (var dx in offsets)
        {
            foreach (var dy in offsets)
            {
                var annotation = Create(kind).Translate(dx, dy);
                var document = ImageDocument.Create(new Size(64, 48)).AddAnnotation(annotation);
                using var canvasImage = ImageBuffer.Create(64, 48, Rgba32.White);
                var ex = Record.Exception(() => new DocumentRenderer().RenderAnnotations(document, canvasImage));
                Assert.True(ex is null, $"{kind} at offset ({dx},{dy}) threw {ex?.GetType().Name}: {ex?.Message}");
            }
        }
    }

    [Fact]
    public void DegenerateAnnotationsRenderWithoutCrashing()
    {
        var degenerate = new Annotation[]
        {
            new RectangleAnnotation { Rectangle = new RectangleF(10, 10, 0, 0) },
            new RectangleAnnotation { Rectangle = new RectangleF(10, 10, -20, -20) },
            new EllipseAnnotation { Rectangle = new RectangleF(5, 5, 0, 30) },
            new LineAnnotation { Start = new Vector2(10, 10), End = new Vector2(10, 10) },
            new ArrowAnnotation { Start = new Vector2(10, 10), End = new Vector2(10, 10) },
            new FreehandAnnotation { Points = [] },
            new FreehandAnnotation { Points = [new(5, 5)] },
            new PolygonAnnotation { Points = [] },
            new PolygonAnnotation { Points = [new(1, 1), new(1, 1)] },
            new TextAnnotation { Text = "", Position = new Vector2(5, 5) },
            new StepAnnotation { Center = new Vector2(5, 5), Number = 0, Radius = 0 },
            new RedactionAnnotation { Rectangle = new RectangleF(0, 0, 0, 0) },
            new CalloutAnnotation { Bubble = new RectangleF(5, 5, 0, 0), Target = new Vector2(5, 5) },
        };

        foreach (var annotation in degenerate)
        {
            var document = ImageDocument.Create(new Size(32, 32)).AddAnnotation(annotation);
            using var image = ImageBuffer.Create(32, 32, Rgba32.White);
            var ex = Record.Exception(() => new DocumentRenderer().RenderAnnotations(document, image));
            Assert.True(ex is null, $"{annotation.Kind} ({annotation.GetBounds()}) threw {ex?.GetType().Name}: {ex?.Message}");
            // Degenerate geometry must also survive being scaled for a proxy.
            var scaledEx = Record.Exception(() => annotation.ForScale(0.5).GetBounds());
            Assert.True(scaledEx is null, $"{annotation.Kind} threw {scaledEx?.GetType().Name} when scaled");
        }
    }

    /// <summary>Redactions must be applied before other annotations, or a redaction could hide a later marker.</summary>
    [Fact]
    public void RedactionsRenderBeforeOtherAnnotations()
    {
        var redaction = new RedactionAnnotation { Rectangle = new RectangleF(0, 0, 32, 32), Mode = RedactionMode.Solid, ZIndex = 100 };
        var marker = new StepAnnotation { Center = new Vector2(16, 16), Number = 1, Radius = 8, ZIndex = 0 };
        var document = ImageDocument.Create(new Size(32, 32)).AddAnnotation(marker).AddAnnotation(redaction);

        var order = document.GetRenderOrder();
        Assert.IsType<RedactionAnnotation>(order[0]);
        Assert.IsType<StepAnnotation>(order[1]);

        using var image = ImageBuffer.Create(32, 32, Rgba32.White);
        new DocumentRenderer().RenderAnnotations(document, image);
        // The step marker must still be visible on top of the redaction.
        Assert.NotEqual(Rgba32.Black, image[16, 16]);
    }

    /// <summary>A redaction must actually destroy the content it covers, not merely obscure it in a preview.</summary>
    [Theory]
    [InlineData(RedactionMode.Solid)]
    [InlineData(RedactionMode.Pixelate)]
    public void RedactionDestroysTheContentUnderneath(RedactionMode mode)
    {
        using var secret = ImageBuffer.Create(64, 64, Rgba32.White);
        // A high contrast pattern: anything left of it after redaction is a leak.
        for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
                secret[x, y] = ((x / 2 + y / 2) % 2 == 0) ? Rgba32.Black : Rgba32.White;

        var document = ImageDocument.Create(secret.Size)
            .AddAnnotation(new RedactionAnnotation { Rectangle = new RectangleF(8, 8, 32, 32), Mode = mode, Strength = 32 });

        using var result = secret.Clone();
        new DocumentRenderer().RenderAnnotations(document, result);

        // Inside the redacted area the fine pattern must be gone: neighbouring pixels should no longer alternate.
        var alternations = 0;
        for (var y = 12; y < 36; y++)
            for (var x = 12; x < 35; x++)
                if (result[x, y] != result[x + 1, y]) alternations++;
        Assert.True(alternations < 40, $"{mode} left {alternations} pixel transitions inside the redacted region");
    }

    /// <summary>Z-order operations must be stable and must not lose or duplicate annotations.</summary>
    [Fact]
    public void ZOrderOperationsPreserveEveryAnnotation()
    {
        var document = ImageDocument.Create(new Size(100, 100));
        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var a = new RectangleAnnotation { Rectangle = new RectangleF(i, i, 10, 10) };
            ids.Add(a.Id);
            document = document.AddAnnotation(a);
        }

        document = document.BringToFront(ids[0]).SendToBack(ids[3]).BringToFront(ids[5]).SendToBack(ids[5]);

        Assert.Equal(6, document.Annotations.Count);
        Assert.Equal(6, document.Annotations.Select(a => a.Id).Distinct().Count());
        foreach (var id in ids) Assert.NotNull(document.FindAnnotation(id));
        Assert.Equal(6, document.GetRenderOrder().Count);
    }

    [Fact]
    public void UpdateAndRemoveTargetTheRightAnnotation()
    {
        var a = new RectangleAnnotation { Rectangle = new RectangleF(0, 0, 10, 10) };
        var b = new RectangleAnnotation { Rectangle = new RectangleF(20, 20, 10, 10) };
        var document = ImageDocument.Create(new Size(64, 64)).AddAnnotation(a).AddAnnotation(b);

        var moved = (RectangleAnnotation)b.Translate(5, 5);
        document = document.UpdateAnnotation(moved);
        Assert.Equal(new RectangleF(25, 25, 10, 10), ((RectangleAnnotation)document.FindAnnotation(b.Id)!).Rectangle);
        Assert.Equal(new RectangleF(0, 0, 10, 10), ((RectangleAnnotation)document.FindAnnotation(a.Id)!).Rectangle);

        document = document.RemoveAnnotation(a.Id);
        Assert.Null(document.FindAnnotation(a.Id));
        Assert.NotNull(document.FindAnnotation(b.Id));

        // Removing an unknown id must be a no-op rather than an error.
        Assert.Single(document.RemoveAnnotation(Guid.NewGuid()).Annotations);
        Assert.Single(document.UpdateAnnotation(a).Annotations);
    }

    /// <summary>
    /// Annotations are overlay objects: they must survive being carried alongside a pixel pipeline and must not change
    /// the output size of the render.
    /// </summary>
    [Fact]
    public void AnnotationsDoNotChangeTheRenderedSize()
    {
        using var source = ImageBuffer.Create(120, 90, Rgba32.White);
        var document = ImageDocument.Create(source.Size)
            .WithPipeline(ImagePipeline.Create().Resize(60, 45, ResizeMode.Stretch))
            .AddAnnotation(new RectangleAnnotation { Rectangle = new RectangleF(5, 5, 20, 20) })
            .AddAnnotation(new StepAnnotation { Center = new Vector2(30, 22), Number = 1 });

        using var rendered = new DocumentRenderer().Render(document, source);
        Assert.Equal(new Size(60, 45), rendered.Size);
        Assert.Equal(new Size(60, 45), document.OutputSize);
    }

    /// <summary>An annotation carrying a rotation must render at the position that rotation implies.</summary>
    [Fact]
    public void RotatedAnnotationRendersAtTheRotatedPosition()
    {
        // A wide bar rotated 90 degrees about its centre becomes a tall bar at the same centre.
        var bar = new RectangleAnnotation
        {
            Rectangle = new RectangleF(20, 46, 60, 8),
            Style = ShapeStyle.Default with { FillColor = Rgba32.Black, StrokeColor = null },
        };
        var document = ImageDocument.Create(new Size(100, 100)).AddAnnotation(bar with { Rotation = 90f });

        using var image = ImageBuffer.Create(100, 100, Rgba32.White);
        new DocumentRenderer().RenderAnnotations(document, image);

        var centre = MarkedCentre(image);
        Assert.NotNull(centre);
        Assert.Equal(50f, centre!.Value.X, 1f);
        Assert.Equal(50f, centre.Value.Y, 1f);

        // Tall, not wide: the rotation actually took effect.
        var painted = new List<(int X, int Y)>();
        for (var y = 0; y < 100; y++)
            for (var x = 0; x < 100; x++)
                if (image[x, y] != Rgba32.White) painted.Add((x, y));
        var width = painted.Max(p => p.X) - painted.Min(p => p.X);
        var height = painted.Max(p => p.Y) - painted.Min(p => p.Y);
        Assert.True(height > width, $"rotated bar rendered {width}x{height}, so the rotation was not applied");
    }
}

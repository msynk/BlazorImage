using System.Drawing;
using BlazorImage.Analysis;
using BlazorImage.Codecs;
using BlazorImage.Geometry;
using BlazorImage.Operations;
using BlazorImage.Pipeline;
using Xunit;

namespace BlazorImage.Tests;

/// <summary>
/// Analysis, placeholder and remaining codec robustness. These paths run over images the application did not create,
/// so degenerate sizes and hostile bytes have to produce a clear result rather than an arbitrary exception.
/// </summary>
public class AdversarialAnalysisTests
{
    private static ImageBuffer Noise(int w, int h, int seed = 1) => AdversarialGeometryTests.Noise(w, h, seed);

    // ---------------------------------------------------------------- smart crop

    /// <summary>Smart cropping must return a rectangle inside the image with the requested aspect ratio.</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(16, 9)]
    [InlineData(9, 16)]
    [InlineData(4, 3)]
    [InlineData(21, 9)]
    public void SmartCropStaysInsideTheImageAndHonoursTheRatio(double rw, double rh)
    {
        foreach (var (w, h) in new[] { (1, 1), (2, 60), (60, 2), (137, 91), (640, 480) })
        {
            using var src = Noise(w, h, w * 13 + h);
            var ratio = new AspectRatio(rw, rh);
            var result = SmartCrop.FindCrop(src, ratio);

            Assert.True(result.Rectangle.Width > 0 && result.Rectangle.Height > 0,
                $"{w}x{h} @ {ratio}: empty crop {result.Rectangle}");
            Assert.True(src.Bounds.Contains(result.Rectangle),
                $"{w}x{h} @ {ratio}: crop {result.Rectangle} escapes the image");

            // The crop must actually be applicable.
            using var cropped = result.ToOperation().Apply(src, OperationContext.Default);
            Assert.Equal(result.Rectangle.Size, cropped.Size);
        }
    }

    /// <summary>Smart cropping is deterministic: the same image must always give the same crop.</summary>
    [Fact]
    public void SmartCropIsDeterministic()
    {
        using var src = Noise(300, 200, 5);
        var first = SmartCrop.FindCrop(src, AspectRatio.Square);
        for (var i = 0; i < 5; i++)
            Assert.Equal(first.Rectangle, SmartCrop.FindCrop(src, AspectRatio.Square).Rectangle);
    }

    /// <summary>A must-include region has to survive: that is the entire contract of the overload.</summary>
    [Fact]
    public void SmartCropRespectsTheMustIncludeRegion()
    {
        using var src = Noise(400, 300, 7);
        var mustInclude = new Rectangle(300, 20, 60, 60);
        var result = SmartCrop.FindCrop(src, AspectRatio.Square, mustInclude);
        Assert.True(result.Rectangle.Contains(mustInclude),
            $"crop {result.Rectangle} does not contain the required region {mustInclude}");
    }

    [Fact]
    public void SmartCropOperationPredictsItsOwnOutputSize()
    {
        foreach (var (w, h) in new[] { (200, 100), (100, 200), (50, 50) })
        {
            using var src = Noise(w, h, w + h);
            var op = new SmartCropOperation(AspectRatio.Ratio16x9);
            using var result = op.Apply(src, OperationContext.Default);
            Assert.Equal(op.GetOutputSize(src.Size), result.Size);
        }
    }

    // ---------------------------------------------------------------- statistics

    [Fact]
    public void StatisticsOfASolidColourAreExact()
    {
        var colour = new Rgba32(40, 80, 120);
        using var src = ImageBuffer.Create(64, 64, colour);
        var stats = ImageStatistics.Measure(src);

        Assert.Equal(colour, stats.AverageColor);
        Assert.Equal(40, stats.Red.Mean, 1);
        Assert.Equal(80, stats.Green.Mean, 1);
        Assert.Equal(120, stats.Blue.Mean, 1);
        Assert.False(stats.HasTransparency);
        Assert.False(stats.IsGrayscale);
        Assert.Equal(64L * 64, stats.TotalPixelCount);
        Assert.Equal(64L * 64, stats.OpaquePixelCount);
    }

    [Fact]
    public void StatisticsRecogniseGrayscaleAndTransparency()
    {
        using var grey = ImageBuffer.Create(32, 32, new Rgba32(90, 90, 90));
        Assert.True(ImageStatistics.Measure(grey).IsGrayscale);

        using var translucent = TestImages.Translucent(32, 32);
        Assert.True(ImageStatistics.Measure(translucent).HasTransparency);
    }

    [Fact]
    public void StatisticsHandleDegenerateImagesAndRegions()
    {
        using var one = ImageBuffer.Create(1, 1, Rgba32.White);
        var stats = ImageStatistics.Measure(one);
        Assert.Equal(255, stats.Red.Mean, 1);
        Assert.Equal(1L, stats.TotalPixelCount);

        using var src = Noise(64, 48, 11);
        // A region clipped to nothing must say so clearly rather than dividing by zero.
        Assert.Throws<ArgumentException>(() => ImageStatistics.Measure(src, new Rectangle(1000, 1000, 10, 10)));
        Assert.Throws<ArgumentException>(() => ImageStatistics.Measure(src, new Rectangle(64, 0, 10, 10)));

        // A partially clipped region is fine.
        var clipped = ImageStatistics.Measure(src, new Rectangle(-10, -10, 30, 30));
        Assert.InRange(clipped.Red.Mean, 0, 255);
    }

    /// <summary>Sampling must not change the answer materially: that is what makes it safe to default to.</summary>
    [Fact]
    public void SampledStatisticsAgreeWithAFullPass()
    {
        using var src = Noise(900, 700, 29);
        var sampled = ImageStatistics.Measure(src, null, 10_000);
        var full = ImageStatistics.Measure(src, null, 0);
        // Sampling one pixel in sixty must not move a channel mean by even one level in 255.
        Assert.True(Math.Abs(full.Red.Mean - sampled.Red.Mean) < 1.5,
            $"sampled red mean {sampled.Red.Mean:0.00} against a full pass of {full.Red.Mean:0.00}");
        Assert.True(Math.Abs(full.Luminance.Mean - sampled.Luminance.Mean) < 1.5,
            $"sampled luminance mean {sampled.Luminance.Mean:0.00} against a full pass of {full.Luminance.Mean:0.00}");
    }

    // ---------------------------------------------------------------- placeholder

    /// <summary>
    /// Both string forms must round-trip. ToString has to be the inverse of Parse, because that is the call a
    /// developer reaches for, and a record's synthesised ToString would silently produce something Parse rejects.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(8, 8)]
    public void PlaceholderRoundTripsThroughItsStringForm(int columns, int rows)
    {
        using var src = Noise(120, 90, 13);
        var placeholder = ImagePlaceholder.Create(src, columns, rows);
        Assert.Equal(placeholder.ToCompactString(), placeholder.ToString());

        foreach (var encoded in new[] { placeholder.ToCompactString(), placeholder.ToString() })
        {
            var parsed = ImagePlaceholder.Parse(encoded, src.Width, src.Height);
            Assert.NotNull(parsed);
            Assert.Equal(columns, parsed!.Columns);
            Assert.Equal(rows, parsed.Rows);

            using var a = placeholder.Render(60, 45);
            using var b = parsed.Render(60, 45);
            Assert.Equal(a.Size, b.Size);
            Assert.True(a.Bytes.SequenceEqual(b.Bytes),
                $"round trip changed the rendered placeholder by {TestImages.MaxChannelDifference(a, b)}");
        }
    }

    [Fact]
    public void PlaceholderRejectsInvalidGridsAndSizes()
    {
        using var src = Noise(32, 32, 17);
        Assert.Throws<ArgumentOutOfRangeException>(() => ImagePlaceholder.Create(src, 0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImagePlaceholder.Create(src, 4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImagePlaceholder.Create(src, 1000, 4));

        var placeholder = ImagePlaceholder.Create(src);
        Assert.Throws<ArgumentOutOfRangeException>(() => placeholder.Render(0, 10).Dispose());
        Assert.Throws<ArgumentOutOfRangeException>(() => placeholder.Render(10, 0).Dispose());
    }

    [Fact]
    public void PlaceholderParseRejectsGarbageInsteadOfThrowing()
    {
        foreach (var value in new[] { null, "", "   ", "not-a-placeholder", "!!!!", "0", new string('z', 500) })
        {
            var ex = Record.Exception(() => ImagePlaceholder.Parse(value, 100, 100));
            Assert.True(ex is null, $"parsing {value ?? "null"} threw {ex?.GetType().Name}");
        }
    }

    /// <summary>
    /// The encoded size depends only on the grid, never on the source image, and matches what the documentation
    /// promises. A placeholder that grew with the image would defeat the purpose of inlining it.
    /// </summary>
    [Theory]
    [InlineData(1, 1, 7)]
    [InlineData(2, 2, 19)]
    [InlineData(4, 4, 67)]
    [InlineData(6, 6, 147)]
    [InlineData(8, 8, 259)]
    public void PlaceholderEncodedSizeDependsOnlyOnTheGrid(int columns, int rows, int expectedLength)
    {
        foreach (var (w, h) in new[] { (32, 32), (1920, 1080), (4032, 3024) })
        {
            using var src = Noise(w, h, w + h);
            var encoded = ImagePlaceholder.Create(src, columns, rows).ToCompactString();
            Assert.Equal(expectedLength, encoded.Length);
        }
    }

    // ---------------------------------------------------------------- codec robustness

    /// <summary>Every managed decoder must reject a hostile file cleanly, whatever the byte pattern.</summary>
    [Theory]
    [InlineData("gif")]
    [InlineData("bmp")]
    [InlineData("png")]
    [InlineData("jpeg")]
    public async Task DecodersRejectHostileHeadersCleanly(string kind)
    {
        var processor = new ImageProcessor();
        var rnd = new Random(31337);
        byte[] header = kind switch
        {
            "gif" => [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a'],
            "bmp" => [(byte)'B', (byte)'M'],
            "png" => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A],
            _ => [0xFF, 0xD8, 0xFF],
        };

        for (var trial = 0; trial < 300; trial++)
        {
            var length = rnd.Next(header.Length, header.Length + 200);
            var data = new byte[length];
            header.CopyTo(data, 0);
            for (var i = header.Length; i < length; i++) data[i] = (byte)rnd.Next(256);

            var ex = await Record.ExceptionAsync(async () => (await processor.DecodeAsync(data)).Dispose());
            Assert.True(ex is null or ImageException,
                $"{kind} trial {trial} threw {ex?.GetType().Name}: {ex?.Message}");
        }
    }

    /// <summary>A declared size far larger than the payload must be refused before any large allocation.</summary>
    [Fact]
    public async Task DecodersRefuseDimensionsBeyondTheLimits()
    {
        var processor = new ImageProcessor(ImageCodecRegistry.CreateDefault(), null, new ImageLimits { MaxPixels = 1_000_000 });

        // A BMP header claiming 30000x30000 in a handful of bytes.
        var bmp = new byte[60];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BitConverter.GetBytes(40).CopyTo(bmp, 14);       // header size
        BitConverter.GetBytes(30000).CopyTo(bmp, 18);    // width
        BitConverter.GetBytes(30000).CopyTo(bmp, 22);    // height
        BitConverter.GetBytes((short)1).CopyTo(bmp, 26); // planes
        BitConverter.GetBytes((short)24).CopyTo(bmp, 28);// bits per pixel

        var ex = await Record.ExceptionAsync(async () => (await processor.DecodeAsync(bmp)).Dispose());
        Assert.True(ex is ImageException, $"an oversized BMP threw {ex?.GetType().Name ?? "nothing"}");
    }

    /// <summary>Identify must never throw, whatever it is handed. It is the safe pre-flight check.</summary>
    [Fact]
    public void IdentifyNeverThrows()
    {
        var processor = new ImageProcessor();
        var rnd = new Random(4242);
        var inputs = new List<byte[]> { Array.Empty<byte>(), new byte[] { 0 }, new byte[] { 0xFF }, new byte[12] };
        for (var i = 0; i < 300; i++)
        {
            var data = new byte[rnd.Next(0, 64)];
            rnd.NextBytes(data);
            inputs.Add(data);
        }

        foreach (var data in inputs)
        {
            var ex = Record.Exception(() => { processor.Identify(data); });
            Assert.True(ex is null, $"Identify threw {ex?.GetType().Name} for {data.Length} bytes");
        }
    }

    /// <summary>Identify must agree with a real decode about the dimensions.</summary>
    [Theory]
    [InlineData(ImageFormat.Png)]
    [InlineData(ImageFormat.Jpeg)]
    [InlineData(ImageFormat.Bmp)]
    public async Task IdentifyAgreesWithDecode(ImageFormat format)
    {
        var processor = new ImageProcessor();
        foreach (var (w, h) in new[] { (1, 1), (17, 5), (64, 64), (200, 137) })
        {
            using var src = Noise(w, h, w * 3 + h);
            var encoded = await processor.EncodeAsync(src, new ImageExportOptions { Format = format, Quality = 0.9 });

            var info = processor.Identify(encoded.Data);
            Assert.NotNull(info);
            Assert.Equal(format, info!.Format);

            using var decoded = await processor.DecodeAsync(encoded.Data);
            Assert.Equal(decoded.Size, info.DisplaySize);
        }
    }

    /// <summary>Format detection must not misidentify one format as another.</summary>
    [Fact]
    public async Task FormatDetectionIsAccurate()
    {
        var processor = new ImageProcessor();
        using var src = Noise(24, 24, 23);
        foreach (var format in new[] { ImageFormat.Png, ImageFormat.Jpeg, ImageFormat.Bmp })
        {
            var encoded = await processor.EncodeAsync(src, new ImageExportOptions { Format = format });
            Assert.Equal(format, ImageFormats.Detect(encoded.Data));
            // Detection must also work from just the leading bytes, which is all a streaming caller has.
            Assert.Equal(format, ImageFormats.Detect(encoded.Data.AsSpan(0, Math.Min(16, encoded.Data.Length))));
        }

        Assert.Equal(ImageFormat.Unknown, ImageFormats.Detect([]));
        Assert.Equal(ImageFormat.Unknown, ImageFormats.Detect([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]));
        Assert.Equal(ImageFormat.Svg, ImageFormats.Detect("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8));
    }

    /// <summary>SVG is untrusted input and must be refused with an explanation rather than rasterised.</summary>
    [Fact]
    public async Task SvgIsRefusedByTheManagedDecoder()
    {
        var processor = new ImageProcessor();
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\"/></svg>"u8.ToArray();
        Assert.Equal(ImageFormat.Svg, ImageFormats.Detect(svg));
        var ex = await Record.ExceptionAsync(async () => (await processor.DecodeAsync(svg)).Dispose());
        Assert.IsAssignableFrom<ImageException>(ex);
    }
}

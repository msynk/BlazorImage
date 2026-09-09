using BlazorImage.Codecs;
using BlazorImage.Codecs.Bmp;
using BlazorImage.Codecs.Gif;
using BlazorImage.Codecs.Jpeg;
using BlazorImage.Codecs.Png;
using BlazorImage.Geometry;
using BlazorImage.Metadata;
using Xunit;

namespace BlazorImage.Tests;

public class PngCodecTests
{
    [Fact]
    public void RoundTripsOpaqueImageLosslessly()
    {
        using var source = TestImages.Gradient(37, 23);
        var encoded = PngEncoder.Encode(source);
        Assert.Equal(ImageFormat.Png, ImageFormats.Detect(encoded.Data));
        using var decoded = PngDecoder.Decode(encoded.Data);
        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
        Assert.Equal(0, TestImages.MaxChannelDifference(source, decoded));
    }

    [Fact]
    public void RoundTripsAlphaLosslessly()
    {
        using var source = TestImages.Translucent(16, 16);
        var encoded = PngEncoder.Encode(source);
        using var decoded = PngDecoder.Decode(encoded.Data);
        Assert.Equal(0, TestImages.MaxChannelDifference(source, decoded));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(9)]
    public void RoundTripsAtEveryCompressionLevel(int level)
    {
        using var source = TestImages.Gradient(29, 17);
        var encoded = PngEncoder.Encode(source, new ImageExportOptions { Format = ImageFormat.Png, PngCompressionLevel = level });
        using var decoded = PngDecoder.Decode(encoded.Data);
        Assert.Equal(0, TestImages.MaxChannelDifference(source, decoded));
    }

    [Fact]
    public void IdentifyReadsDimensionsWithoutDecoding()
    {
        using var source = TestImages.Translucent(21, 13);
        var encoded = PngEncoder.Encode(source);
        var info = new PngDecoder().Identify(encoded.Data);
        Assert.NotNull(info);
        Assert.Equal(21, info!.Width);
        Assert.Equal(13, info.Height);
        Assert.True(info.HasAlpha);
    }

    [Fact]
    public void OpaqueImagesEncodeWithoutAlphaChannel()
    {
        using var source = TestImages.Gradient(8, 8);
        var encoded = PngEncoder.Encode(source);
        var info = new PngDecoder().Identify(encoded.Data)!;
        Assert.False(info.HasAlpha);
    }

    [Fact]
    public void PreservesExifWhenPolicyIsPreserve()
    {
        using var source = TestImages.Gradient(8, 8);
        var exif = new ExifData().WithPrimary(new ExifDirectory().With(ExifTag.Make, ExifValue.FromString("BlazorImage")));
        source.Metadata = new ImageMetadata(exif);
        var encoded = PngEncoder.Encode(source, new ImageExportOptions { Format = ImageFormat.Png, Metadata = MetadataPolicy.Preserve });
        using var decoded = PngDecoder.Decode(encoded.Data);
        Assert.Equal("BlazorImage", decoded.Metadata?.Exif?.Make);
    }

    [Fact]
    public void RemovesExifByDefault()
    {
        using var source = TestImages.Gradient(8, 8);
        source.Metadata = new ImageMetadata(new ExifData().WithPrimary(new ExifDirectory().With(ExifTag.Make, ExifValue.FromString("Camera"))));
        var encoded = PngEncoder.Encode(source);
        using var decoded = PngDecoder.Decode(encoded.Data);
        Assert.Null(decoded.Metadata?.Exif);
    }

    [Fact]
    public void RejectsTruncatedData()
    {
        using var source = TestImages.Gradient(32, 32);
        var encoded = PngEncoder.Encode(source);
        var truncated = encoded.Data[..(encoded.Data.Length / 2)];
        Assert.Throws<ImageDecodeException>(() => PngDecoder.Decode(truncated));
    }

    [Fact]
    public void RejectsOversizedImages()
    {
        using var source = TestImages.Gradient(64, 64);
        var encoded = PngEncoder.Encode(source);
        var limits = new ImageLimits { MaxWidth = 32, MaxHeight = 32 };
        Assert.Throws<ImageLimitExceededException>(() => PngDecoder.Decode(encoded.Data, new DecodeOptions { Limits = limits }));
    }

    [Fact]
    public void AppliesExifOrientationOnDecode()
    {
        using var source = TestImages.Quadrants(8, 8);
        source.Metadata = new ImageMetadata(ExifData.FromOrientation(Orientation.Rotate90));
        var encoded = PngEncoder.Encode(source, new ImageExportOptions { Format = ImageFormat.Png, Metadata = MetadataPolicy.Preserve });

        // Preserve resets the stored orientation to normal, so re-inject it to simulate a camera file.
        var reOriented = PngContainer.WriteMetadata(encoded.Data, new ImageMetadata(ExifData.FromOrientation(Orientation.Rotate90)));
        using var decoded = PngDecoder.Decode(reOriented, new DecodeOptions { AutoOrient = true });
        Assert.Equal(Orientation.Normal, decoded.Metadata!.Orientation);
        // Top-left of the rotated image comes from the bottom-left of the original (blue).
        Assert.Equal(new Rgba32(0, 0, 255), decoded[0, 0]);
    }
}

public class BmpCodecTests
{
    [Fact]
    public void RoundTripsOpaqueImage()
    {
        using var source = TestImages.Gradient(33, 17);
        var encoded = BmpEncoder.Encode(source);
        using var decoded = BmpDecoder.Decode(encoded.Data);
        Assert.Equal(0, TestImages.MaxChannelDifference(source, decoded));
    }

    [Fact]
    public void RoundTripsAlphaImage()
    {
        using var source = TestImages.Translucent(9, 7);
        var encoded = BmpEncoder.Encode(source);
        using var decoded = BmpDecoder.Decode(encoded.Data);
        Assert.Equal(0, TestImages.MaxChannelDifference(source, decoded));
    }

    [Fact]
    public void IdentifiesDimensions()
    {
        using var source = TestImages.Gradient(11, 5);
        var encoded = BmpEncoder.Encode(source);
        var info = new BmpDecoder().Identify(encoded.Data)!;
        Assert.Equal(11, info.Width);
        Assert.Equal(5, info.Height);
    }
}

public class JpegCodecTests
{
    [Fact]
    public void RoundTripsGradientWithinToleranceAtHighQuality()
    {
        using var source = TestImages.Gradient(64, 48);
        var encoded = JpegEncoder.Encode(source, new ImageExportOptions { Format = ImageFormat.Jpeg, Quality = 0.95 });
        Assert.Equal(ImageFormat.Jpeg, ImageFormats.Detect(encoded.Data));
        using var decoded = JpegDecoder.Decode(encoded.Data);
        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
        Assert.True(TestImages.MeanChannelDifference(source, decoded) < 3.0, $"Mean difference {TestImages.MeanChannelDifference(source, decoded):0.##} is too high.");
    }

    [Fact]
    public void DecodesOwnSubsampledOutput()
    {
        using var source = TestImages.Gradient(70, 34);
        var encoded = JpegEncoder.Encode(source, new ImageExportOptions { Format = ImageFormat.Jpeg, Quality = 0.8 });
        using var decoded = JpegDecoder.Decode(encoded.Data);
        Assert.Equal(70, decoded.Width);
        Assert.Equal(34, decoded.Height);
        Assert.True(TestImages.MeanChannelDifference(source, decoded) < 8.0);
    }

    [Fact]
    public void LowerQualityProducesSmallerFiles()
    {
        using var source = TestImages.Gradient(128, 128);
        var high = JpegEncoder.Encode(source, new ImageExportOptions { Format = ImageFormat.Jpeg, Quality = 0.95 });
        var low = JpegEncoder.Encode(source, new ImageExportOptions { Format = ImageFormat.Jpeg, Quality = 0.4 });
        Assert.True(low.Length < high.Length, $"low={low.Length} high={high.Length}");
    }

    [Fact]
    public void CompositesTransparencyOverBackground()
    {
        using var source = ImageBuffer.Create(16, 16, Rgba32.Transparent);
        var encoded = JpegEncoder.Encode(source, new ImageExportOptions { Format = ImageFormat.Jpeg, Quality = 0.95, Background = new Rgba32(255, 0, 0) });
        using var decoded = JpegDecoder.Decode(encoded.Data);
        var p = decoded[8, 8];
        Assert.True(p.R > 200 && p.G < 60 && p.B < 60, $"Expected red background, got {p}");
        Assert.Equal(255, p.A);
    }

    [Fact]
    public void IdentifyReadsDimensions()
    {
        using var source = TestImages.Gradient(37, 21);
        var encoded = JpegEncoder.Encode(source);
        var info = new JpegDecoder().Identify(encoded.Data)!;
        Assert.Equal(37, info.Width);
        Assert.Equal(21, info.Height);
    }

    [Fact]
    public void WritesAndReadsExifWithPreservePolicy()
    {
        using var source = TestImages.Gradient(16, 16);
        source.Metadata = new ImageMetadata(new ExifData().WithPrimary(new ExifDirectory().With(ExifTag.Model, ExifValue.FromString("Test Model"))));
        var encoded = JpegEncoder.Encode(source, new ImageExportOptions { Format = ImageFormat.Jpeg, Metadata = MetadataPolicy.Preserve });
        using var decoded = JpegDecoder.Decode(encoded.Data);
        Assert.Equal("Test Model", decoded.Metadata?.Exif?.Model);
    }

    [Fact]
    public void RejectsGarbage()
    {
        var garbage = new byte[512];
        garbage[0] = 0xFF; garbage[1] = 0xD8; garbage[2] = 0xFF;
        Assert.Throws<ImageDecodeException>(() => JpegDecoder.Decode(garbage));
    }
}

public class FormatDetectionTests
{
    [Theory]
    [InlineData("image/jpeg", ImageFormat.Jpeg)]
    [InlineData("image/png", ImageFormat.Png)]
    [InlineData("image/webp", ImageFormat.WebP)]
    [InlineData("image/avif", ImageFormat.Avif)]
    [InlineData("image/svg+xml", ImageFormat.Svg)]
    [InlineData("image/jpeg; charset=binary", ImageFormat.Jpeg)]
    [InlineData("nonsense", ImageFormat.Unknown)]
    public void MapsMimeTypes(string mime, ImageFormat expected) => Assert.Equal(expected, ImageFormats.FromMimeType(mime));

    [Theory]
    [InlineData("photo.JPG", ImageFormat.Jpeg)]
    [InlineData("a.b.webp", ImageFormat.WebP)]
    [InlineData(".png", ImageFormat.Png)]
    [InlineData("noext", ImageFormat.Unknown)]
    public void MapsFileNames(string name, ImageFormat expected) => Assert.Equal(expected, ImageFormats.FromFileName(name));

    [Fact]
    public void DetectsWebPMagic()
    {
        var data = new byte[16];
        "RIFF"u8.CopyTo(data);
        "WEBP"u8.CopyTo(data.AsSpan(8));
        Assert.Equal(ImageFormat.WebP, ImageFormats.Detect(data));
    }

    [Fact]
    public void DetectsAvifBrand()
    {
        var data = new byte[]
        {
            0, 0, 0, 0x1C, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'a', (byte)'v', (byte)'i', (byte)'f',
            0, 0, 0, 0, (byte)'a', (byte)'v', (byte)'i', (byte)'f',
        };
        Assert.Equal(ImageFormat.Avif, ImageFormats.Detect(data));
    }

    [Fact]
    public void DetectsSvg()
    {
        Assert.Equal(ImageFormat.Svg, ImageFormats.Detect("  <svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8));
        Assert.Equal(ImageFormat.Svg, ImageFormats.Detect("<?xml version=\"1.0\"?><svg/>"u8));
    }

    [Fact]
    public void ReturnsUnknownForEmptyOrShortData()
    {
        Assert.Equal(ImageFormat.Unknown, ImageFormats.Detect([]));
        Assert.Equal(ImageFormat.Unknown, ImageFormats.Detect([0x00, 0x01]));
    }
}

public class GifCodecTests
{
    // A 4x4 GIF89a: 2x2 blocks of red, green, blue, white, LZW encoded with a 4-entry global palette.
    private static byte[] SampleGif =>
    [
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x04, 0x00, 0x04, 0x00, 0x81, 0x00, 0x00,
        0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF,
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x04, 0x00, 0x00,
        0x02, 0x07, 0x04, 0x12, 0x86, 0x22, 0x33, 0xEC, 0x0A, 0x00, 0x3B,
    ];

    [Fact]
    public void DecodesFirstFrame()
    {
        using var img = GifDecoder.Decode(SampleGif);
        Assert.Equal(4, img.Width);
        Assert.Equal(4, img.Height);
        Assert.Equal(new Rgba32(255, 0, 0), img[0, 0]);
        Assert.Equal(new Rgba32(0, 255, 0), img[3, 0]);
        Assert.Equal(new Rgba32(0, 0, 255), img[0, 3]);
        Assert.Equal(new Rgba32(255, 255, 255), img[3, 3]);
    }

    [Fact]
    public void IdentifiesDimensions()
    {
        var info = new GifDecoder().Identify(SampleGif)!;
        Assert.Equal(4, info.Width);
        Assert.Equal(4, info.Height);
    }
}

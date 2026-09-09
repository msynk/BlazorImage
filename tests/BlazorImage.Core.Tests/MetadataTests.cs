using BlazorImage.Codecs;
using BlazorImage.Codecs.Jpeg;
using BlazorImage.Codecs.Png;
using BlazorImage.Geometry;
using BlazorImage.Metadata;
using Xunit;

namespace BlazorImage.Tests;

public class ExifTests
{
    /// <summary>Builds EXIF data resembling what a phone camera writes, including GPS.</summary>
    private static ExifData BuildCameraExif() => new ExifData()
        .WithPrimary(new ExifDirectory()
            .With(ExifTag.Make, ExifValue.FromString("BlazorPhone"))
            .With(ExifTag.Model, ExifValue.FromString("BP-9 Pro"))
            .With(ExifTag.Software, ExifValue.FromString("BP OS 3.1"))
            .With(ExifTag.Copyright, ExifValue.FromString("© 2026 Example"))
            .With(ExifTag.Orientation, ExifValue.FromShort(6)))
        .WithExif(new ExifDirectory()
            .With(ExifTag.DateTimeOriginal, ExifValue.FromString("2026:04:17 09:31:44"))
            .With(ExifTag.ExposureTime, ExifValue.FromRational((1, 250)))
            .With(ExifTag.FNumber, ExifValue.FromRational((18, 10)))
            .With(ExifTag.Iso, ExifValue.FromShort(200))
            .With(ExifTag.FocalLength, ExifValue.FromRational((26, 1)))
            .With(ExifTag.LensModel, ExifValue.FromString("BP 26mm f/1.8"))
            .With(ExifTag.BodySerialNumber, ExifValue.FromString("SN-4417-XY"))
            .With(ExifTag.UserComment, ExifValue.FromString("private note"))
            .With(ExifTag.PixelXDimension, ExifValue.FromLong(4032))
            .With(ExifTag.PixelYDimension, ExifValue.FromLong(3024)))
        .WithGps(new ExifDirectory()
            .With(ExifTag.GpsLatitudeRef, ExifValue.FromString("N"))
            .With(ExifTag.GpsLatitude, ExifValue.FromRational((51, 1), (30, 1), (2613, 100)))
            .With(ExifTag.GpsLongitudeRef, ExifValue.FromString("W"))
            .With(ExifTag.GpsLongitude, ExifValue.FromRational((0, 1), (7, 1), (3924, 100)))
            .With(ExifTag.GpsAltitudeRef, ExifValue.FromShort(0))
            .With(ExifTag.GpsAltitude, ExifValue.FromRational((35, 1))));

    [Fact]
    public void RoundTripsThroughTiffSerialisation()
    {
        var original = BuildCameraExif();
        var restored = ExifData.Parse(original.ToTiff());
        Assert.NotNull(restored);
        Assert.Equal("BlazorPhone", restored!.Make);
        Assert.Equal("BP-9 Pro", restored.Model);
        Assert.Equal("© 2026 Example", restored.Copyright);
        Assert.Equal(Orientation.Rotate90, restored.Orientation);
        Assert.Equal(200, restored.Iso);
        Assert.Equal(1.0 / 250, restored.ExposureTime!.Value, 6);
        Assert.Equal(1.8, restored.FNumber!.Value, 3);
        Assert.Equal(26, restored.FocalLength!.Value, 3);
        Assert.Equal("BP 26mm f/1.8", restored.LensModel);
        Assert.Equal(4032, restored.PixelXDimension);
    }

    [Fact]
    public void DecodesGpsCoordinates()
    {
        var exif = BuildCameraExif();
        var location = exif.Location;
        Assert.NotNull(location);
        Assert.Equal(51.5072583, location!.Value.Latitude, 4);
        // West is negative.
        Assert.Equal(-0.1275, location.Value.Longitude, 3);
        Assert.Equal(35, location.Value.Altitude);
    }

    [Fact]
    public void ParsesDatesAndRejectsMalformedOnes()
    {
        Assert.Equal(new DateTimeOffset(2026, 4, 17, 9, 31, 44, TimeSpan.Zero), BuildCameraExif().DateTaken);
        Assert.Null(ExifData.ParseDate("not a date"));
        Assert.Null(ExifData.ParseDate("2026:13:45 99:99:99"));
        Assert.Null(ExifData.ParseDate(null));
        Assert.Null(ExifData.ParseDate("2026:02:30 10:00:00"));
    }

    [Fact]
    public void ParsesDateOffsets()
    {
        var exif = new ExifData().WithExif(new ExifDirectory()
            .With(ExifTag.DateTimeOriginal, ExifValue.FromString("2026:04:17 09:31:44"))
            .With(ExifTag.OffsetTimeOriginal, ExifValue.FromString("+02:00")));
        Assert.Equal(TimeSpan.FromHours(2), exif.DateTaken!.Value.Offset);
    }

    [Fact]
    public void StripSensitiveRemovesLocationAndIdentifiersButKeepsTheCamera()
    {
        var stripped = BuildCameraExif().StripSensitive();
        Assert.Null(stripped.Location);
        Assert.False(stripped.HasLocation);
        Assert.Null(stripped.Exif.Get(ExifTag.BodySerialNumber));
        Assert.Null(stripped.Exif.Get(ExifTag.UserComment));
        // Descriptive data survives, which is the point of this policy.
        Assert.Equal("BlazorPhone", stripped.Make);
        Assert.Equal("© 2026 Example", stripped.Copyright);
        Assert.Equal(200, stripped.Iso);
    }

    [Fact]
    public void OrientationCanBeReadAndReset()
    {
        var exif = BuildCameraExif();
        Assert.Equal(Orientation.Rotate90, exif.Orientation);
        Assert.Equal(Orientation.Normal, exif.WithOrientation(Orientation.Normal).Orientation);
    }

    [Fact]
    public void HandlesTruncatedAndCorruptDataWithoutThrowing()
    {
        var valid = BuildCameraExif().ToTiff();
        for (var length = 1; length < valid.Length; length += Math.Max(1, valid.Length / 40))
        {
            var truncated = valid[..length];
            // Must never throw, however mangled the input.
            var result = ExifData.Parse(truncated);
            _ = result?.Make;
            _ = result?.Location;
        }
        Assert.Null(ExifData.Parse([]));
        Assert.Null(ExifData.Parse([0xFF, 0xFF, 0xFF, 0xFF]));
    }

    [Fact]
    public void IgnoresAbsurdOffsetsRatherThanReadingOutOfBounds()
    {
        var data = BuildCameraExif().ToTiff();
        // Point the first IFD entry's value offset far beyond the buffer.
        var corrupted = (byte[])data.Clone();
        for (var i = 8 + 2 + 8; i + 4 <= corrupted.Length && i < 40; i++) corrupted[i] = 0xFE;
        var result = ExifData.Parse(corrupted);
        // Some fields may be lost, but parsing must complete safely.
        _ = result?.Make;
    }

    [Fact]
    public void MetadataPolicyControlsWhatSurvives()
    {
        var metadata = new ImageMetadata(BuildCameraExif(), new byte[] { 1, 2, 3, 4 }, "<x:xmpmeta/>"u8.ToArray(), ImageFormat.Jpeg);

        var removed = metadata.Apply(MetadataPolicy.Remove, 100, 80);
        Assert.Null(removed.Exif);
        Assert.True(removed.IccProfile.IsEmpty);
        Assert.True(removed.Xmp.IsEmpty);

        var stripped = metadata.Apply(MetadataPolicy.StripSensitive, 100, 80);
        Assert.NotNull(stripped.Exif);
        Assert.Null(stripped.Exif!.Location);
        Assert.Equal("BlazorPhone", stripped.Exif.Make);
        Assert.False(stripped.IccProfile.IsEmpty);
        // XMP can carry location too, so this policy drops it.
        Assert.True(stripped.Xmp.IsEmpty);
        Assert.Equal(100, stripped.Exif.PixelXDimension);

        var preserved = metadata.Apply(MetadataPolicy.Preserve, 100, 80);
        Assert.NotNull(preserved.Exif!.Location);
        Assert.False(preserved.Xmp.IsEmpty);
        Assert.Equal(100, preserved.Exif.PixelXDimension);

        // The orientation tag describes the pixels in the buffer, so a policy carries it through rather than
        // forcing it to normal. Forcing it would export a sideways image whenever the caller decoded with
        // AutoOrient off, with nothing left in the file to correct it.
        Assert.Equal(Orientation.Rotate90, stripped.Exif.Orientation);
        Assert.Equal(Orientation.Rotate90, preserved.Exif.Orientation);

        // A buffer whose pixels have been brought upright reports normal, and that is what gets written.
        var upright = metadata.WithExif(metadata.Exif!.WithOrientation(Orientation.Normal));
        Assert.Equal(Orientation.Normal, upright.Apply(MetadataPolicy.Preserve, 100, 80).Exif!.Orientation);
    }
}

public class ContainerTests
{
    [Fact]
    public void JpegMetadataSurvivesAWriteReadRoundTrip()
    {
        using var image = TestImages.Gradient(32, 32);
        var jpeg = JpegEncoder.Encode(image, new ImageExportOptions { Format = ImageFormat.Jpeg, Quality = 0.9 }).Data;
        var metadata = new ImageMetadata(
            new ExifData().WithPrimary(new ExifDirectory().With(ExifTag.Artist, ExifValue.FromString("A Photographer"))),
            sourceFormat: ImageFormat.Jpeg);

        var withMetadata = JpegContainer.WriteMetadata(jpeg, metadata);
        Assert.Equal(ImageFormat.Jpeg, ImageFormats.Detect(withMetadata));
        var read = JpegContainer.ReadMetadata(withMetadata);
        Assert.Equal("A Photographer", read.Exif?.Artist);

        // The image must still decode after metadata surgery.
        using var decoded = JpegDecoder.Decode(withMetadata);
        Assert.Equal(32, decoded.Width);
    }

    [Fact]
    public void WritingJpegMetadataTwiceDoesNotAccumulateSegments()
    {
        using var image = TestImages.Gradient(16, 16);
        var jpeg = JpegEncoder.Encode(image).Data;
        var metadata = new ImageMetadata(new ExifData().WithPrimary(new ExifDirectory().With(ExifTag.Make, ExifValue.FromString("One"))));
        var once = JpegContainer.WriteMetadata(jpeg, metadata);
        var twice = JpegContainer.WriteMetadata(once, metadata);
        // The second write replaces the first rather than appending, so sizes match closely.
        Assert.Equal(once.Length, twice.Length);
        Assert.Equal("One", JpegContainer.ReadMetadata(twice).Exif?.Make);
    }

    [Fact]
    public void PngMetadataSurvivesAWriteReadRoundTrip()
    {
        using var image = TestImages.Gradient(24, 24);
        var png = PngEncoder.Encode(image).Data;
        var icc = new byte[] { 10, 20, 30, 40, 50 };
        var metadata = new ImageMetadata(
            new ExifData().WithPrimary(new ExifDirectory().With(ExifTag.Software, ExifValue.FromString("BlazorImage"))),
            icc,
            "<x:xmpmeta xmlns:x='adobe:ns:meta/'/>"u8.ToArray(),
            ImageFormat.Png);

        var withMetadata = PngContainer.WriteMetadata(png, metadata);
        var read = PngContainer.ReadMetadata(withMetadata);
        Assert.Equal("BlazorImage", read.Exif?.Software);
        Assert.Equal(icc, read.IccProfile.ToArray());
        Assert.Contains("xmpmeta", System.Text.Encoding.UTF8.GetString(read.Xmp.Span), StringComparison.Ordinal);

        using var decoded = PngDecoder.Decode(withMetadata);
        Assert.Equal(24, decoded.Width);
        Assert.Equal(0, TestImages.MaxChannelDifference(image, decoded));
    }

    [Fact]
    public void RejectsNonImageDataClearly()
    {
        var garbage = new byte[64];
        Assert.Throws<ImageEncodeException>(() => JpegContainer.WriteMetadata(garbage, ImageMetadata.Empty));
        Assert.Throws<ImageEncodeException>(() => PngContainer.WriteMetadata(garbage, ImageMetadata.Empty));
    }

    [Fact]
    public void ReadsWebPDimensionsFromEachHeaderKind()
    {
        // A minimal lossy WebP header: RIFF....WEBPVP8 ....<frame header with 64x48>
        var webp = new byte[] {
            (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0x20, 0, 0, 0,
            (byte)'W', (byte)'E', (byte)'B', (byte)'P',
            (byte)'V', (byte)'P', (byte)'8', (byte)' ', 0x10, 0, 0, 0,
            0, 0, 0, 0x9D, 0x01, 0x2A, 64, 0, 48, 0, 0, 0, 0, 0, 0, 0,
        };
        var size = WebPContainer.ReadSize(webp);
        Assert.NotNull(size);
        Assert.Equal(64, size!.Value.Width);
        Assert.Equal(48, size.Value.Height);
    }
}

public class OrientationDecodeTests
{
    [Theory]
    [InlineData(Orientation.Rotate90)]
    [InlineData(Orientation.Rotate180)]
    [InlineData(Orientation.Rotate270)]
    [InlineData(Orientation.FlipHorizontal)]
    [InlineData(Orientation.FlipVertical)]
    [InlineData(Orientation.Transpose)]
    [InlineData(Orientation.Transverse)]
    public void AutoOrientMakesEveryOrientationUpright(Orientation orientation)
    {
        // Start from an upright image, store it rotated, and tag it so a decoder should undo the rotation.
        using var upright = TestImages.Quadrants(16, 16);
        using var stored = new Operations.Transforms.OrientationOperation(orientation.Inverse())
            .Apply(upright, Operations.OperationContext.Default);

        var png = PngEncoder.Encode(stored).Data;
        var tagged = PngContainer.WriteMetadata(png, new ImageMetadata(ExifData.FromOrientation(orientation)));

        using var decoded = PngDecoder.Decode(tagged, new DecodeOptions { AutoOrient = true });
        Assert.Equal(upright.Size, decoded.Size);
        Assert.Equal(0, TestImages.MaxChannelDifference(upright, decoded));
        Assert.Equal(Orientation.Normal, decoded.Metadata!.Orientation);
    }

    [Fact]
    public void AutoOrientCanBeTurnedOff()
    {
        using var source = TestImages.Quadrants(8, 8);
        var png = PngEncoder.Encode(source).Data;
        var tagged = PngContainer.WriteMetadata(png, new ImageMetadata(ExifData.FromOrientation(Orientation.Rotate90)));
        using var decoded = PngDecoder.Decode(tagged, new DecodeOptions { AutoOrient = false });
        Assert.Equal(0, TestImages.MaxChannelDifference(source, decoded));
        // The orientation is reported rather than applied, so the caller can decide.
        Assert.Equal(Orientation.Rotate90, decoded.Metadata!.Orientation);
    }
}

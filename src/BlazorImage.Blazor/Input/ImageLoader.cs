using System.Text.Json.Serialization;
using BlazorImage.Capabilities;
using BlazorImage.Codecs;
using BlazorImage.Codecs.Browser;
using BlazorImage.Pipeline;
using BlazorImage.Interop;
using BlazorImage.Memory;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace BlazorImage.Input;

/// <summary>
/// Loads images from any browser source into an <see cref="ImageBuffer"/>: file inputs, drag and drop, the clipboard,
/// the camera, remote URLs and raw bytes.
/// </summary>
public sealed class ImageLoader
{
    private readonly ImageProcessorFactory _processors;
    private readonly BrowserImageInterop _interop;
    private readonly IImageCapabilityProvider _capabilities;

    public ImageLoader(ImageProcessorFactory processors, BrowserImageInterop interop, IImageCapabilityProvider capabilities)
    {
        _processors = processors ?? throw new ArgumentNullException(nameof(processors));
        _interop = interop ?? throw new ArgumentNullException(nameof(interop));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
    }

    /// <summary>Maximum encoded size accepted for a single image. Defaults to 64 MB.</summary>
    public long MaxSourceBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Loads an image from any source. The caller owns the returned buffer.</summary>
    public async ValueTask<ImageBuffer> LoadAsync(ImageSource source, DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var processor = await _processors.GetAsync(cancellationToken).ConfigureAwait(false);
        options ??= new DecodeOptions();
        if (options.FileName is null && source.FileName is not null) options = options with { FileName = source.FileName };

        if (source.IsRemote)
        {
            var (url, withCredentials) = source.GetRemoteRequest();
            return await LoadFromUrlAsync(processor, url, withCredentials, options, cancellationToken).ConfigureAwait(false);
        }

        using var buffer = await source.ReadAsync(MaxSourceBytes, cancellationToken).ConfigureAwait(false);
        if (buffer.Length == 0) throw new ImageDecodeException($"'{source.FileName ?? "The file"}' is empty.");
        return await processor.DecodeAsync(buffer.Memory, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads an image from a file input or drop zone.</summary>
    public ValueTask<ImageBuffer> LoadAsync(IBrowserFile file, DecodeOptions? options = null, CancellationToken cancellationToken = default)
        => LoadAsync(ImageSource.FromBrowserFile(file), options, cancellationToken);

    /// <summary>Loads an image from raw encoded bytes.</summary>
    public ValueTask<ImageBuffer> LoadAsync(ReadOnlyMemory<byte> bytes, DecodeOptions? options = null, CancellationToken cancellationToken = default)
        => LoadAsync(ImageSource.FromBytes(bytes), options, cancellationToken);

    /// <summary>
    /// Loads several images, reporting progress and collecting per-file failures instead of aborting on the first bad file.
    /// </summary>
    public async ValueTask<IReadOnlyList<ImageLoadResult>> LoadManyAsync(
        IEnumerable<ImageSource> sources,
        DecodeOptions? options = null,
        IProgress<(int Completed, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var list = sources.ToList();
        var results = new List<ImageLoadResult>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var image = await LoadAsync(list[i], options, cancellationToken).ConfigureAwait(false);
                results.Add(ImageLoadResult.Success(list[i], image));
            }
            catch (OperationCanceledException)
            {
                foreach (var r in results) r.Image?.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                results.Add(ImageLoadResult.Failure(list[i], ex));
            }
            progress?.Report((i + 1, list.Count));
        }
        return results;
    }

    /// <summary>
    /// Loads the image currently on the clipboard, or null when there is none. Requires a user gesture and clipboard
    /// permission in most browsers.
    /// </summary>
    public async ValueTask<ImageBuffer?> LoadFromClipboardAsync(DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        var capabilities = await _capabilities.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!capabilities.SupportsClipboardRead)
            throw new ImageCapabilityException("This browser cannot read images from the clipboard. Offer a file picker or a paste target instead.");
        var processor = await _processors.GetAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var result = await _interop.InvokeForBytesAsync<ClipboardInfo>("readClipboardImage", cancellationToken).ConfigureAwait(false);
            if (!result.HasValue || result.Bytes.Length == 0) return null;
            return await processor.DecodeAsync(result.Bytes.Memory, options ?? new DecodeOptions(), cancellationToken).ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            throw new ImageCapabilityException($"Reading the clipboard failed. The browser may have denied permission: {ex.Message}", ex);
        }
    }

    /// <summary>Fetches and decodes a remote image. Requires the remote server to permit cross-origin reads.</summary>
    private async ValueTask<ImageBuffer> LoadFromUrlAsync(ImageProcessor processor, string url, bool withCredentials, DecodeOptions options, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri))
            throw new ArgumentException($"'{url}' is not a valid URL.", nameof(url));
        if (uri.IsAbsoluteUri && uri.Scheme is not ("http" or "https" or "blob" or "data"))
            throw new ArgumentException($"The scheme '{uri.Scheme}' is not allowed for image URLs. Use http, https, blob or data.", nameof(url));
        try
        {
            using var result = await _interop.InvokeForBytesAsync<ClipboardInfo>("fetchImage", cancellationToken, url, withCredentials).ConfigureAwait(false);
            if (!result.HasValue || result.Bytes.Length == 0)
                throw new ImageDecodeException($"Fetching '{url}' returned no data.");
            return await processor.DecodeAsync(result.Bytes.Memory, options, cancellationToken).ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            throw new ImageDecodeException(
                $"Could not fetch '{url}'. The most common cause is a missing CORS header on the remote server; a browser will not let a page read pixels it cannot access. The browser reported: {ex.Message}", ex);
        }
    }

    private sealed class ClipboardInfo
    {
        [JsonPropertyName("mimeType")] public string? MimeType { get; set; }
        [JsonPropertyName("byteLength")] public long ByteLength { get; set; }
    }
}

/// <summary>The outcome of loading one image in a batch.</summary>
public sealed class ImageLoadResult : IDisposable
{
    private ImageLoadResult(ImageSource source, ImageBuffer? image, Exception? error)
    {
        Source = source;
        Image = image;
        Error = error;
    }

    public static ImageLoadResult Success(ImageSource source, ImageBuffer image) => new(source, image, null);
    public static ImageLoadResult Failure(ImageSource source, Exception error) => new(source, null, error);

    public ImageSource Source { get; }

    /// <summary>The decoded image, or null when loading failed. Owned by the caller.</summary>
    public ImageBuffer? Image { get; }

    public Exception? Error { get; }
    public bool IsSuccess => Image is not null;
    public string? FileName => Source.FileName;

    public void Dispose() => Image?.Dispose();
}

/// <summary>
/// Builds an <see cref="ImageProcessor"/> wired to whichever codecs this environment actually supports. Browser codecs
/// are registered on top of the managed ones so they take precedence where available.
/// </summary>
public sealed class ImageProcessorFactory
{
    private readonly BrowserImageInterop _interop;
    private readonly IImageCapabilityProvider _capabilities;
    private readonly BlazorImageOptions _options;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ImageProcessor? _processor;

    public ImageProcessorFactory(BrowserImageInterop interop, IImageCapabilityProvider capabilities, BlazorImageOptions? options = null)
    {
        _interop = interop ?? throw new ArgumentNullException(nameof(interop));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _options = options ?? new BlazorImageOptions();
    }

    /// <summary>
    /// Returns the processor for this environment, creating it on first use. In a browser this prefers the native
    /// codecs; elsewhere it falls back to the managed ones so headless and prerendered code still works.
    /// </summary>
    public async ValueTask<ImageProcessor> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_processor is { } cached) return cached;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_processor is { } existing) return existing;
            var capabilities = await _capabilities.GetAsync(cancellationToken).ConfigureAwait(false);
            var registry = ImageCodecRegistry.CreateDefault();
            if (capabilities.Environment is ImageEnvironment.BrowserWebAssembly or ImageEnvironment.BrowserServer)
            {
                var decodable = capabilities.DecodableFormats.Where(f => f != ImageFormat.Svg).ToArray();
                var encodable = capabilities.EncodableFormats.Where(f => f != ImageFormat.Bmp).ToArray();
                if (decodable.Length > 0) registry.AddDecoder(new BrowserImageDecoder(_interop, decodable));
                if (encodable.Length > 0) registry.AddEncoder(new BrowserImageEncoder(_interop, encodable));
            }
            var processor = new ImageProcessor(registry, _options.Allocator ?? PooledPixelAllocator.Shared, _options.Limits);
            // Only cache once a real browser answered; a prerender pass must not pin the managed-only registry.
            if (capabilities.Environment != ImageEnvironment.Prerendering) _processor = processor;
            return processor;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Drops the cached processor so the next call re-detects capabilities.</summary>
    public void Invalidate() => _processor = null;
}

/// <summary>Options for the BlazorImage services.</summary>
public sealed class BlazorImageOptions
{
    /// <summary>Safety limits applied to decoding and processing. Defaults to <see cref="ImageLimits.Default"/>.</summary>
    public ImageLimits Limits { get; set; } = ImageLimits.Default;

    /// <summary>Allocator used for pixel buffers. Defaults to the shared pooled allocator.</summary>
    public IPixelAllocator? Allocator { get; set; }

    /// <summary>Maximum encoded bytes accepted for a single uploaded image. Defaults to 64 MB.</summary>
    public long MaxSourceBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// Largest working resolution used for interactive editing. Larger images are edited through a downscaled proxy and
    /// the edit is replayed at full resolution on export. Null uses the device-appropriate default.
    /// </summary>
    public int? MaxInteractiveDimension { get; set; }
}

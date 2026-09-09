using System.Drawing;
using BlazorImage.Drawing;
using BlazorImage.Geometry;
using BlazorImage.Memory;
using BlazorImage.Operations;
using BlazorImage.Operations.Transforms;
using BlazorImage.Pipeline;

namespace BlazorImage.Editing;

/// <summary>
/// An interactive editing session over one image: the original pixels, the current non-destructive document, the undo
/// history and a live preview.
/// </summary>
/// <remarks>
/// <para>
/// Interactive editing on a 24 megapixel photo cannot re-run the pipeline at full resolution on every slider tick. This
/// session keeps a downscaled <b>proxy</b> sized for the display, previews edits on that, and replays the edit at full
/// resolution only when exporting. Because operations know how to rescale themselves, the preview and the export are
/// the same edit at two resolutions rather than two different code paths.
/// </para>
/// <para>
/// Preview rendering is serialised and cancellable: a new request cancels the one in flight, so dragging a slider never
/// queues work faster than it completes.
/// </para>
/// </remarks>
public sealed class ImageEditSession : IDisposable
{
    private readonly IPixelAllocator _allocator;
    private readonly ImageLimits _limits;
    private readonly DocumentRenderer _renderer;
    private ImageBuffer _original;
    private ImageBuffer? _proxy;
    private ImageBuffer? _preview;
    private CancellationTokenSource? _renderCts;
    private readonly SemaphoreSlim _renderLock = new(1, 1);
    private bool _disposed;

    /// <summary>Creates a session that takes ownership of <paramref name="original"/>.</summary>
    public ImageEditSession(ImageBuffer original, ImageEditSessionOptions? options = null)
    {
        _original = original ?? throw new ArgumentNullException(nameof(original));
        var o = options ?? new ImageEditSessionOptions();
        _allocator = o.Allocator ?? PooledPixelAllocator.Shared;
        _limits = o.Limits ?? ImageLimits.Default;
        _renderer = new DocumentRenderer(o.TextRasterizer);
        MaxProxyDimension = o.MaxProxyDimension;
        UpdateProxyGeometry();
        History = new EditHistory(ImageDocument.Create(original), o.MaxHistoryDepth);
        History.Changed += _ => MarkPreviewStale();
    }

    /// <summary>The untouched source image. Never modified.</summary>
    public ImageBuffer Original => _original;

    /// <summary>Size of the source image.</summary>
    public Size SourceSize => _original.Size;

    /// <summary>Undo/redo over the document.</summary>
    public EditHistory History { get; }

    /// <summary>The current non-destructive edit.</summary>
    public ImageDocument Document => History.Current;

    /// <summary>Size the exported image will have.</summary>
    public Size OutputSize => Document.OutputSize;

    /// <summary>
    /// Longest edge of the working proxy. Editing happens at this resolution; export always uses the full image.
    /// </summary>
    public int MaxProxyDimension { get; }

    /// <summary>
    /// The scale of the proxy relative to the original, 1 when the image is small enough to edit directly. Valid from
    /// construction, before the first preview render.
    /// </summary>
    public double ProxyScale { get; private set; } = 1.0;

    /// <summary>
    /// Size of the working proxy. Equals <see cref="SourceSize"/> when the image is edited at full resolution.
    /// Valid from construction, before the first preview render.
    /// </summary>
    public Size ProxySize { get; private set; }

    /// <summary>True when the image is large enough that editing happens on a downscaled proxy.</summary>
    public bool UsesProxy => ProxyScale < 1.0;

    /// <summary>The most recently rendered preview, or null before the first render. Owned by the session.</summary>
    public ImageBuffer? Preview => _preview;

    /// <summary>True when the preview no longer matches the document.</summary>
    public bool IsPreviewStale { get; private set; } = true;

    /// <summary>Raised after a preview render completes.</summary>
    public event Action<ImageBuffer>? PreviewUpdated;

    /// <summary>Raised when the document changes, before the new preview is available.</summary>
    public event Action<ImageDocument>? DocumentChanged;

    /// <summary>Applies a change to the document and records it in the history.</summary>
    public void Apply(Func<ImageDocument, ImageDocument> change, string label)
    {
        ArgumentNullException.ThrowIfNull(change);
        var next = change(Document);
        if (ReferenceEquals(next, Document)) return;
        History.Push(next, label);
        DocumentChanged?.Invoke(next);
    }

    /// <summary>
    /// Applies a change without adding a history entry, replacing the current one. Use while a control is being dragged,
    /// then call <see cref="Commit"/> when the interaction ends.
    /// </summary>
    public void ApplyTransient(Func<ImageDocument, ImageDocument> change, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        var next = change(Document);
        if (ReferenceEquals(next, Document)) return;
        History.Amend(next, label);
        DocumentChanged?.Invoke(next);
    }

    /// <summary>Turns the current amended state into a permanent history entry.</summary>
    public void Commit(string label)
    {
        History.Push(Document, label);
        DocumentChanged?.Invoke(Document);
    }

    /// <summary>Adds an operation and records it.</summary>
    public void AddOperation(IImageOperation operation, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Apply(d => d.AddOperation(operation), label ?? operation.Name);
    }

    /// <summary>Adds an annotation and records it.</summary>
    public void AddAnnotation(Annotations.Annotation annotation, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        Apply(d => d.AddAnnotation(annotation), label ?? $"Add {annotation.Kind.ToLowerInvariant()}");
    }

    /// <summary>Undoes one step.</summary>
    public void Undo()
    {
        if (!History.CanUndo) return;
        History.Undo();
        DocumentChanged?.Invoke(Document);
    }

    /// <summary>Redoes one step.</summary>
    public void Redo()
    {
        if (!History.CanRedo) return;
        History.Redo();
        DocumentChanged?.Invoke(Document);
    }

    /// <summary>Discards every edit.</summary>
    public void Reset()
    {
        History.Push(ImageDocument.Create(_original), "Reset");
        DocumentChanged?.Invoke(Document);
    }

    /// <summary>Replaces the source image, discarding all edits and history.</summary>
    public void ReplaceSource(ImageBuffer image, bool disposeExisting = true)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var old = _original;
        _original = image;
        DisposeProxy();
        DisposePreview();
        UpdateProxyGeometry();
        History.Clear(ImageDocument.Create(image));
        if (disposeExisting && !ReferenceEquals(old, image)) old.Dispose();
        MarkPreviewStale();
        DocumentChanged?.Invoke(Document);
    }

    private void MarkPreviewStale() => IsPreviewStale = true;

    /// <summary>
    /// Renders the preview at proxy resolution. Cancels any render already running, so rapid edits never pile up.
    /// Returns the preview buffer, which the session continues to own.
    /// </summary>
    /// <remarks>
    /// A superseded call throws <see cref="OperationCanceledException"/>. Callers that fire a render on every slider
    /// tick should catch and ignore it: it means a newer render has taken over, not that anything failed.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The render was superseded by a newer one, or the caller cancelled.</exception>
    public async ValueTask<ImageBuffer> RenderPreviewAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Each call owns exactly one token source and is the only code that disposes it. Publishing it before
        // cancelling the previous one means a render that is still queued behind the lock is cancelled too.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancelQuietly(Interlocked.Exchange(ref _renderCts, cts));

        try
        {
            await _renderLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.CompareExchange(ref _renderCts, null, cts);
            cts.Dispose();
            throw;
        }

        try
        {
            cts.Token.ThrowIfCancellationRequested();
            var proxy = EnsureProxy();
            var document = UsesProxy ? Document.ForScale(ProxyScale) : Document;
            var rendered = await _renderer.RenderAsync(document, proxy, new PipelineExecutionOptions
            {
                Allocator = _allocator,
                Limits = _limits,
                CancellationToken = cts.Token,
            }, ProxyScale).ConfigureAwait(false);

            var old = _preview;
            _preview = rendered;
            IsPreviewStale = false;
            old?.Dispose();
            PreviewUpdated?.Invoke(rendered);
            return rendered;
        }
        finally
        {
            Interlocked.CompareExchange(ref _renderCts, null, cts);
            _renderLock.Release();
            cts.Dispose();
        }
    }

    /// <summary>
    /// Cancels a token source that another call owns. The owner may have disposed it already, which is not an error:
    /// a disposed source belongs to a render that has finished and therefore needs no cancelling.
    /// </summary>
    private static void CancelQuietly(CancellationTokenSource? cts)
    {
        if (cts is null) return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* the owning render already completed */ }
    }

    /// <summary>
    /// Renders the final image at full resolution. Slower than the preview and allocates a full-size buffer, so call it
    /// on export rather than on every change. The caller owns the returned buffer.
    /// </summary>
    public ValueTask<ImageBuffer> RenderFullAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer.RenderAsync(Document, _original, new PipelineExecutionOptions
        {
            Allocator = _allocator,
            Limits = _limits,
            CancellationToken = cancellationToken,
            Progress = progress,
        });
    }

    /// <summary>Maps a point in preview coordinates to the full-resolution output coordinates.</summary>
    public System.Numerics.Vector2 PreviewToOutput(System.Numerics.Vector2 point)
        => UsesProxy ? point / (float)ProxyScale : point;

    /// <summary>Maps a point in full-resolution output coordinates to preview coordinates.</summary>
    public System.Numerics.Vector2 OutputToPreview(System.Numerics.Vector2 point)
        => UsesProxy ? point * (float)ProxyScale : point;

    /// <summary>
    /// Resolves the proxy size and scale from the source dimensions alone. This is arithmetic, not pixels, so it runs
    /// as soon as a source image is set: pointer coordinates must map correctly from the first frame, long before the
    /// first preview render has finished.
    /// </summary>
    private void UpdateProxyGeometry()
    {
        var longest = Math.Max(_original.Width, _original.Height);
        if (MaxProxyDimension <= 0 || longest <= MaxProxyDimension)
        {
            ProxySize = _original.Size;
            ProxyScale = 1.0;
            return;
        }
        var nominal = MaxProxyDimension / (double)longest;
        var width = Math.Max(1, (int)Math.Round(_original.Width * nominal));
        var height = Math.Max(1, (int)Math.Round(_original.Height * nominal));
        ProxySize = new Size(width, height);
        // Derive the effective scale from the longest edge, the one actually pinned to MaxProxyDimension, so the
        // rounding error in the mapping is as small as it can be.
        ProxyScale = _original.Width >= _original.Height
            ? width / (double)_original.Width
            : height / (double)_original.Height;
    }

    private ImageBuffer EnsureProxy()
    {
        if (_proxy is { } existing) return existing;
        if (!UsesProxy) return _original;
        _proxy = new ResizeOperation(ProxySize.Width, ProxySize.Height, ResizeMode.Stretch, ResamplingFilter.Auto)
            .Apply(_original, new OperationContext(_allocator, _limits));
        return _proxy;
    }

    private void DisposeProxy()
    {
        var proxy = _proxy;
        _proxy = null;
        if (proxy is not null && !ReferenceEquals(proxy, _original)) proxy.Dispose();
    }

    private void DisposePreview()
    {
        var preview = _preview;
        _preview = null;
        preview?.Dispose();
    }

    /// <summary>Releases the source image, proxy and preview.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Cancel but do not dispose: the token source belongs to the render that created it, and that render is
        // still reading its token. It disposes it on the way out.
        CancelQuietly(Interlocked.Exchange(ref _renderCts, null));
        DisposePreview();
        DisposeProxy();
        _original.Dispose();
        _renderLock.Dispose();
    }
}

/// <summary>Options for an <see cref="ImageEditSession"/>.</summary>
public sealed class ImageEditSessionOptions
{
    /// <summary>
    /// Longest edge of the interactive proxy. 2048 keeps editing smooth on phones while staying sharp on a desktop
    /// display. Set to 0 to always edit at full resolution.
    /// </summary>
    public int MaxProxyDimension { get; set; } = 2048;

    /// <summary>Number of undo steps kept.</summary>
    public int MaxHistoryDepth { get; set; } = 100;

    /// <summary>Allocator used for proxies and previews.</summary>
    public IPixelAllocator? Allocator { get; set; }

    /// <summary>Limits applied to rendering.</summary>
    public ImageLimits? Limits { get; set; }

    /// <summary>Rasteriser used for text annotations.</summary>
    public ITextRasterizer? TextRasterizer { get; set; }
}

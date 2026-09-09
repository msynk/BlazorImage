# BlazorImage

Browser-side image processing for Blazor. Load, transform, annotate and export images entirely on the user's device,
from strongly typed C#.

No server round trips. No base64. No JavaScript image library.

```csharp
var result = await processor.ProcessAsync(
    uploadedFile,
    ImagePipeline.Create()
        .AutoOrient()
        .Cover(512, 512)
        .Sharpen(0.3f),
    new ImageExportOptions
    {
        Format      = ImageFormat.WebP,
        Quality     = 0.82,
        Metadata    = MetadataPolicy.StripSensitive,
        MaxFileSize = 120 * 1024,
    });
```

That runs in the browser. The original file never leaves it, the phone's rotation is applied, the GPS coordinates are
stripped, and the result is a WebP that fits the size budget you asked for.

---

## Why this exists

Blazor developers who need to crop an avatar or shrink an upload have had two options: send the full-resolution file to
a server, or assemble several JavaScript libraries behind hand-written interop. This is a third option: one coherent,
Blazor-native image processing platform where the engine itself is C#.

- **Non-destructive by construction.** An edit is a list of operations, not a mutated buffer. Undo costs a few hundred
  bytes, previews are cheap, and any earlier step can be changed without redoing the rest.
- **The engine is this project.** The resampler, rasteriser, colour pipeline, TrueType text engine, and the PNG, JPEG,
  BMP and GIF codecs are implemented here. The browser is used only for what only it can do: WebP and AVIF.
- **Large images stay usable.** Interactive editing runs on a proxy sized for the display and replays at full resolution
  on export, so a 24 megapixel photo does not stall the interface.
- **Privacy is the default.** Metadata is removed unless you ask for it. Location data cannot leak into an exported
  avatar by accident.
- **Nothing about the browser is assumed.** Format support is probed by actually encoding a pixel. When a browser
  substitutes PNG for the AVIF you requested, you get an exception, not a silent surprise.
- **Headless first.** Every capability works without the editor component, so the same pipeline runs in an upload
  handler or a console tool.

## Install

```bash
dotnet add package BlazorImage.Blazor    # browser integration: codecs, capabilities, loading, export
dotnet add package BlazorImage.Editor    # the editor component (pulls in BlazorImage.Blazor)
dotnet add package BlazorImage.Core      # engine only, no Blazor dependency
```

Register the services in `Program.cs`:

```csharp
using BlazorImage;   // AddBlazorImage lives here

builder.Services.AddBlazorImage();
```

Add the stylesheet if you use the editor:

```html
<link rel="stylesheet" href="_content/BlazorImage.Editor/blazorimage-editor.css" />
```

## The simple path

```razor
@inject ImageLoader Loader
@inject ImageExporter Exporter

<InputFile OnChange="OnChange" accept="image/*" />

@code {
    async Task OnChange(InputFileChangeEventArgs e)
    {
        using var image = await Loader.LoadAsync(e.File);
        using var thumb = await ImagePipeline.Create()
            .AutoOrient()
            .MaxSize(400, 400)
            .ExecuteAsync(image);

        await Exporter.DownloadAsync(thumb, "thumbnail",
            new ImageExportOptions { Format = ImageFormat.WebP, Quality = 0.85 });
    }
}
```

## The full editor

```razor
<ImageEditor @ref="editor" />
<button @onclick="() => editor.DownloadAsync(&quot;edited&quot;)">Save</button>
```

Crop, rotate, adjustments, filters, drawing, annotations, redaction, layers, history and export, in one component. It is
themeable through CSS custom properties, fully localisable, keyboard accessible, touch friendly and right-to-left aware.
Every part of its UI can be replaced while keeping the engine:

```razor
<ImageEditor Strings="german" Direction="rtl" Class="bi-theme-dark">
    <Toolbar Context="editor">
        <button @onclick="() => editor.SetTool(EditorToolKind.Crop)">Crop</button>
        <button disabled="@(!editor.CanUndo)" @onclick="editor.Undo">Undo</button>
    </Toolbar>
</ImageEditor>
```

## What is in the box

| Area | What you get |
| --- | --- |
| Loading | `IBrowserFile`, byte arrays, streams, data URLs, object URLs, remote URLs, clipboard, drag and drop, camera |
| Decoding | PNG, JPEG, BMP and GIF in managed code; PNG, JPEG, WebP, AVIF and GIF through the browser |
| Encoding | PNG, JPEG and BMP in managed code; PNG, JPEG, WebP and AVIF through the browser |
| Transforms | Crop, aspect crop, resize in six modes, rotate, flip, skew, affine and perspective warps |
| Adjustments | Brightness, contrast, saturation, hue, exposure, gamma, levels, temperature, tint, vibrance, shadows, highlights, opacity |
| Filters | Grayscale, sepia, invert, posterize, threshold, blur, sharpen, pixelate, noise, median denoise, vignette, custom convolution and colour matrices |
| Analysis | Histograms, luminance statistics, dominant colour palettes, auto-levels, auto-contrast, saliency-based smart cropping, low-quality placeholders |
| Drawing | Anti-aliased paths, strokes with caps, joins and dashes, gradients, ellipses, polygons, arrows, freehand, text |
| Annotations | Rectangle, ellipse, line, arrow, freehand, polygon, text, highlight, callout, numbered step, image stamp, redaction |
| Metadata | EXIF read and write, GPS, camera data, orientation, ICC profiles, XMP, three privacy policies |
| Pipelines | Composition, optimisation, lazy execution, cancellation, progress, batch processing |
| Editing | Non-destructive documents, undo and redo, memory-aware history, proxy previews |

## Measured, not claimed

Every number below was measured with BenchmarkDotNet on desktop .NET 10. WebAssembly is slower in absolute terms, but the
relative picture holds. See [docs/performance.md](docs/performance.md) for the full tables and how to reproduce them.

| Operation | Image | Time | Allocated |
| --- | --- | ---: | ---: |
| Thumbnail to 200px | 6000 × 4000 | 52 ms | 23 KB |
| Downscale to 1280px, Lanczos | 3840 × 2160 | 68 ms | 180 KB |
| Upload pipeline: orient, cap at 1920, sharpen | 4000 × 3000 | 231 ms | 237 KB |
| Four adjustments fused into one pass | 3840 × 2160 | 189 ms | 4.8 KB |
| JPEG encode, quality 85 | 3840 × 2160 | 175 ms | 6.7 MB |
| PNG decode | 3840 × 2160 | 104 ms | 37 MB |

Four of those numbers exist because measuring changed the design:

- Thumbnailing a 24 megapixel photo took **368 ms**. Adding an exact integer box prepass before the high quality filter
  brought it to **52 ms**, a 7× improvement, with 10× fewer allocations and no visible quality change.
- Fusing a resize with the crop that follows it made that pair **2.4× faster**, because the pixels that the crop throws
  away are never resampled.
- The JPEG encoder built three full-resolution float planes, costing **116 MB** for a 4K image. Streaming it one MCU row
  at a time cut that to **6.7 MB**, a 17× reduction, with no loss of speed.
- Filling a path with hundreds of contours took **1075 ms**, because every sub-scanline walked every edge. An active
  edge table brought it to **107 ms**, a 10× improvement that every text annotation benefits from.

## Tested

- **285 unit tests** covering geometry, colour, transforms, resampling accuracy, filters, drawing, the TrueType parser,
  text layout, EXIF, containers, codecs, pipelines, history, image analysis and the optimiser.
- **24 browser tests** driving a real Chrome against the demo application: capability probing, decode and encode through
  the browser, canvas painting, pointer drawing, undo and redo, live adjustments and accessibility attributes.

Several of these found real bugs during development, and each now has a regression test:

| Bug | Symptom |
| --- | --- |
| EXIF text written as ASCII, read as UTF-8 | A copyright symbol became "?" and accented names were mangled |
| Fused adjustments skipped intermediate clipping | A blown-out highlight recovered detail it should not have |
| Auto-contrast used the reciprocal gamma | Dark images got darker instead of brighter |
| Skin-tone detection accepted neutral grey | Smart crop chased the grey background instead of the subject |
| Capability probe encoded a canvas with no context | Every format was reported unencodable |

```bash
dotnet test tests/BlazorImage.Core.Tests        # fast, no browser needed
dotnet test tests/BlazorImage.Browser.Tests     # starts the demo app and drives a real browser
dotnet run --project tests/BlazorImage.Benchmarks -c Release -- --filter "*"
```

## Demo

```bash
dotnet run --project samples/BlazorImage.Demo
```

Nine worked scenarios rather than a page of buttons: an avatar editor with a size budget, screenshot annotation with
redaction, an upload optimiser reporting real byte savings, a product image processor, a social composer with burned-in
captions, a batch processor with bounded concurrency, the full editor, a headless page using no components at all, and a
capability report for the current browser.

## Where it runs

| Host | Support |
| --- | --- |
| Blazor WebAssembly | Everything |
| Blazor Web App, Interactive WebAssembly | Everything |
| Blazor Web App, Interactive Auto | Everything once interactive |
| Blazor Web App, Interactive Server | Everything, with pixel data crossing the circuit; see [docs/browser-support.md](docs/browser-support.md) |
| Prerendering | Managed codecs only, and capability detection reports `Prerendering`; call browser features from `OnAfterRenderAsync` |
| ASP.NET Core, console, worker | Headless API with the managed codecs, no browser involved |

## Documentation

- [Getting started](docs/getting-started.md)
- [Architecture](docs/architecture.md)
- [Pipelines](docs/pipelines.md)
- [The editor component](docs/editor.md)
- [Headless processing](docs/headless.md)
- [Formats and capabilities](docs/formats.md)
- [Metadata and privacy](docs/metadata-and-privacy.md)
- [Performance](docs/performance.md)
- [Security](docs/security.md)
- [Browser support](docs/browser-support.md)
- [Extending](docs/extending.md)
- [Troubleshooting](docs/troubleshooting.md)

## Licence

MIT. See [LICENSE](LICENSE).

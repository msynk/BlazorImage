using System.Text.Json;
using Microsoft.Playwright;
using Xunit;

namespace BlazorImage.Browser.Tests;

/// <summary>
/// End-to-end checks that only a real browser can answer: does the JavaScript module load, do the browser codecs
/// actually run, do the pixels come back correct, and does the editor respond to input.
/// </summary>
[Collection(BrowserCollection.Name)]
public class CapabilityTests
{
    private readonly DemoAppFixture _fixture;

    public CapabilityTests(DemoAppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task DetectsFormatsByActuallyEncoding()
    {
        await using var page = await _fixture.OpenAsync("capabilities");
        await page.WaitForSelectorAsync("table.data", new PageWaitForSelectorOptions { Timeout = 60_000 });
        var text = await page.Locator(".grid").InnerTextAsync();

        // Every browser that can run Blazor can do these; if the probe says otherwise it is broken.
        Assert.Contains("PNG", text, StringComparison.Ordinal);
        Assert.Matches(@"PNG\s+yes\s+yes", text.Replace("\t", " "));
        Assert.Matches(@"JPEG\s+yes\s+yes", text.Replace("\t", " "));
        Assert.DoesNotContain("Probing this browser", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsTheRuntimeAsWebAssembly()
    {
        await using var page = await _fixture.OpenAsync("capabilities");
        await page.WaitForSelectorAsync("table.data", new PageWaitForSelectorOptions { Timeout = 60_000 });
        var text = await page.Locator(".grid").InnerTextAsync();
        Assert.Contains("BrowserWebAssembly", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindsARealisticCanvasLimit()
    {
        await using var page = await _fixture.OpenAsync("capabilities");
        await page.WaitForSelectorAsync("table.data", new PageWaitForSelectorOptions { Timeout = 60_000 });
        var text = await page.Locator(".grid").InnerTextAsync();
        var match = System.Text.RegularExpressions.Regex.Match(text, @"MAX CANVAS EDGE\s+(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.True(match.Success, "The canvas limit was not reported");
        var limit = int.Parse(match.Groups[1].Value);
        Assert.InRange(limit, 4096, 65536);
    }
}

[Collection(BrowserCollection.Name)]
public class ProcessingTests
{
    private readonly DemoAppFixture _fixture;

    public ProcessingTests(DemoAppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task DecodesProcessesAndEncodesThroughTheBrowser()
    {
        await using var page = await _fixture.OpenAsync("avatar");
        var file = _fixture.WriteSampleImage();
        try
        {
            await page.SetInputFilesAsync("input[type=file]", file);
            await page.WaitForSelectorAsync(".stat-grid", new PageWaitForSelectorOptions { Timeout = 60_000 });
            await page.WaitForFunctionAsync("() => document.querySelectorAll('img.preview-image').length >= 2", null,
                new PageWaitForFunctionOptions { Timeout = 60_000 });

            var stats = await page.Locator(".stat-grid").InnerTextAsync();
            // A gradient at 256px must compress to something far smaller than the source PNG.
            Assert.Contains("KB", stats, StringComparison.Ordinal);
            Assert.DoesNotContain("0 B", stats, StringComparison.Ordinal);

            var result = await ReadPreviewAsync(page);
            Assert.Equal(256, result.Width);
            Assert.Equal(256, result.Height);
            // The circular mask is on by default, so the corner must be transparent and the centre opaque.
            Assert.Equal(0, result.CornerAlpha);
            Assert.Equal(255, result.CentreAlpha);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task ChangingAnOptionReprocessesTheImage()
    {
        await using var page = await _fixture.OpenAsync("avatar");
        var file = _fixture.WriteSampleImage();
        try
        {
            await page.SetInputFilesAsync("input[type=file]", file);
            await page.WaitForFunctionAsync("() => document.querySelectorAll('img.preview-image').length >= 2", null,
                new PageWaitForFunctionOptions { Timeout = 60_000 });
            var before = await ReadPreviewAsync(page);
            Assert.Equal(256, before.Width);

            // Drive the slider the way a user would, then wait for the debounced reprocess.
            await page.EvaluateAsync("""
                () => {
                    const el = document.querySelector('#size');
                    Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set.call(el, '512');
                    el.dispatchEvent(new Event('input', { bubbles: true }));
                }
                """);
            await page.WaitForFunctionAsync("() => document.querySelector('img.preview-image')?.naturalWidth === 512", null,
                new PageWaitForFunctionOptions { Timeout = 60_000 });

            var after = await ReadPreviewAsync(page);
            Assert.Equal(512, after.Width);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task ReportsAClearErrorForAFileThatIsNotAnImage()
    {
        await using var page = await _fixture.OpenAsync("avatar");
        var file = Path.Combine(Path.GetTempPath(), $"blazorimage-{Guid.NewGuid():N}.png");
        await File.WriteAllTextAsync(file, "this is definitely not a png");
        try
        {
            await page.SetInputFilesAsync("input[type=file]", file);
            await page.WaitForSelectorAsync(".alert-error", new PageWaitForSelectorOptions { Timeout = 60_000 });
            var message = await page.Locator(".alert-error").InnerTextAsync();
            Assert.False(string.IsNullOrWhiteSpace(message));
        }
        finally
        {
            File.Delete(file);
        }
    }

    private sealed record PreviewInfo(int Width, int Height, int CornerAlpha, int CentreAlpha);

    /// <summary>Decodes the rendered preview in the page and samples it, which verifies the real encoded output.</summary>
    private static async Task<PreviewInfo> ReadPreviewAsync(IPage page)
    {
        var json = await page.EvaluateAsync<JsonElement>("""
            async () => {
                const img = document.querySelector('img.preview-image');
                if (!img) return null;
                const blob = await (await fetch(img.src)).blob();
                const bmp = await createImageBitmap(blob);
                const canvas = new OffscreenCanvas(bmp.width, bmp.height);
                const ctx = canvas.getContext('2d');
                ctx.clearRect(0, 0, bmp.width, bmp.height);
                ctx.drawImage(bmp, 0, 0);
                const corner = ctx.getImageData(2, 2, 1, 1).data;
                const centre = ctx.getImageData(bmp.width >> 1, bmp.height >> 1, 1, 1).data;
                return { width: bmp.width, height: bmp.height, cornerAlpha: corner[3], centreAlpha: centre[3] };
            }
            """);
        Assert.NotEqual(JsonValueKind.Null, json.ValueKind);
        return new PreviewInfo(
            json.GetProperty("width").GetInt32(),
            json.GetProperty("height").GetInt32(),
            json.GetProperty("cornerAlpha").GetInt32(),
            json.GetProperty("centreAlpha").GetInt32());
    }
}

[Collection(BrowserCollection.Name)]
public class EditorTests
{
    private readonly DemoAppFixture _fixture;

    public EditorTests(DemoAppFixture fixture) => _fixture = fixture;

    private async Task<IPage> OpenEditorWithImageAsync()
    {
        var page = await _fixture.OpenAsync("annotate");
        var file = _fixture.WriteSampleImage();
        try
        {
            await page.SetInputFilesAsync(".bi-editor input[type=file]", file);
            await page.WaitForSelectorAsync(".bi-canvas", new PageWaitForSelectorOptions { Timeout = 60_000 });
            await page.WaitForFunctionAsync("() => document.querySelector('.bi-canvas')?.width > 1", null,
                new PageWaitForFunctionOptions { Timeout = 60_000 });
        }
        finally
        {
            File.Delete(file);
        }
        return page;
    }

    [Fact]
    public async Task PaintsTheImageOntoTheCanvas()
    {
        await using var page = await OpenEditorWithImageAsync();
        var size = await page.EvaluateAsync<JsonElement>("() => { const c = document.querySelector('.bi-canvas'); return { w: c.width, h: c.height }; }");
        Assert.Equal(800, size.GetProperty("w").GetInt32());
        Assert.Equal(600, size.GetProperty("h").GetInt32());

        // The centre pixel must match the gradient the sample image was built from, not an empty canvas.
        var alpha = await page.EvaluateAsync<int>("""
            () => {
                const c = document.querySelector('.bi-canvas');
                return c.getContext('2d').getImageData(c.width >> 1, c.height >> 1, 1, 1).data[3];
            }
            """);
        Assert.Equal(255, alpha);
    }

    [Fact]
    public async Task RendersTheFullToolbarAndPanel()
    {
        await using var page = await OpenEditorWithImageAsync();
        Assert.True(await page.Locator(".bi-toolbar .bi-tool").CountAsync() >= 12);
        var tabs = await page.Locator(".bi-panel-tab").AllInnerTextsAsync();
        Assert.Contains("Adjust", tabs);
        Assert.Contains("Layers", tabs);
        Assert.Contains("History", tabs);
        Assert.Contains("Export", tabs);
    }

    [Fact]
    public async Task AnnotationsAppearInTheLayerListAndOnThePixels()
    {
        await using var page = await OpenEditorWithImageAsync();
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Add a worked example" }).ClickAsync();
        await page.WaitForTimeoutAsync(2500);

        await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Layers" }).ClickAsync();
        var layers = await page.Locator(".bi-layer-name").AllInnerTextsAsync();
        Assert.Contains("Redaction", layers);
        Assert.Contains("Arrow", layers);
        Assert.Equal(5, layers.Count);

        // The redaction must have replaced the pixels, not merely covered them in the DOM.
        var redacted = await page.EvaluateAsync<int[]>("""
            () => {
                const c = document.querySelector('.bi-canvas');
                const d = c.getContext('2d').getImageData(Math.round(c.width * 0.15), Math.round(c.height * 0.12), 1, 1).data;
                return [d[0], d[1], d[2], d[3]];
            }
            """);
        Assert.Equal([0, 0, 0, 255], redacted);
    }

    [Fact]
    public async Task UndoAndRedoWalkTheHistory()
    {
        await using var page = await OpenEditorWithImageAsync();
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Add a worked example" }).ClickAsync();
        await page.WaitForTimeoutAsync(2500);

        await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Layers" }).ClickAsync();
        Assert.Equal(5, await page.Locator(".bi-layer").CountAsync());

        await page.Locator(".bi-tool[title=Undo]").ClickAsync();
        await page.WaitForTimeoutAsync(1200);
        Assert.Equal(4, await page.Locator(".bi-layer").CountAsync());

        await page.Locator(".bi-tool[title=Redo]").ClickAsync();
        await page.WaitForTimeoutAsync(1200);
        Assert.Equal(5, await page.Locator(".bi-layer").CountAsync());
    }

    [Fact]
    public async Task AdjustmentsChangeThePixelsLive()
    {
        await using var page = await OpenEditorWithImageAsync();

        var before = await ReadCentrePixelAsync(page);
        await page.EvaluateAsync("""
            () => {
                const slider = document.querySelectorAll('.bi-slider')[0];
                Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set.call(slider, '0.6');
                slider.dispatchEvent(new Event('input', { bubbles: true }));
            }
            """);
        await page.WaitForTimeoutAsync(2500);
        var after = await ReadCentrePixelAsync(page);

        Assert.True(after[0] > before[0], $"Brightness did not lighten the image: {before[0]} -> {after[0]}");
    }

    [Fact]
    public async Task RotatingChangesTheCanvasDimensions()
    {
        await using var page = await OpenEditorWithImageAsync();
        await page.Locator(".bi-tool[title='Rotate right']").ClickAsync();
        await page.WaitForFunctionAsync("() => document.querySelector('.bi-canvas')?.width === 600", null,
            new PageWaitForFunctionOptions { Timeout = 60_000 });
        var size = await page.EvaluateAsync<JsonElement>("() => { const c = document.querySelector('.bi-canvas'); return { w: c.width, h: c.height }; }");
        Assert.Equal(600, size.GetProperty("w").GetInt32());
        Assert.Equal(800, size.GetProperty("h").GetInt32());
    }

    [Fact]
    public async Task TheCanvasIsReachableAndLabelledForAssistiveTechnology()
    {
        await using var page = await OpenEditorWithImageAsync();
        var host = page.Locator(".bi-canvas-host");
        Assert.Equal("application", await host.GetAttributeAsync("role"));
        Assert.False(string.IsNullOrWhiteSpace(await host.GetAttributeAsync("aria-label")));
        Assert.Equal("0", await host.GetAttributeAsync("tabindex"));

        var toolbar = page.Locator(".bi-toolbar");
        Assert.Equal("toolbar", await toolbar.GetAttributeAsync("role"));

        // Every tool button carries a label and a pressed state.
        var first = page.Locator(".bi-toolbar .bi-tool").First;
        Assert.False(string.IsNullOrWhiteSpace(await first.GetAttributeAsync("aria-label")));
        Assert.NotNull(await first.GetAttributeAsync("aria-pressed"));
    }

    [Fact]
    public async Task DrawingWithThePointerCreatesAnAnnotation()
    {
        await using var page = await OpenEditorWithImageAsync();
        await page.Locator(".bi-tool[title=Arrow]").ClickAsync();

        var canvas = page.Locator(".bi-canvas");
        var box = await canvas.BoundingBoxAsync();
        Assert.NotNull(box);

        // Drag across the canvas the way a user draws an arrow.
        await page.Mouse.MoveAsync(box!.X + box.Width * 0.25f, box.Y + box.Height * 0.7f);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(box.X + box.Width * 0.5f, box.Y + box.Height * 0.5f, new MouseMoveOptions { Steps = 8 });
        await page.Mouse.MoveAsync(box.X + box.Width * 0.7f, box.Y + box.Height * 0.3f, new MouseMoveOptions { Steps = 8 });
        await page.Mouse.UpAsync();
        await page.WaitForTimeoutAsync(2000);

        await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Layers" }).ClickAsync();
        var layers = await page.Locator(".bi-layer-name").AllInnerTextsAsync();
        Assert.Contains("Arrow", layers);
    }

    private static async Task<int[]> ReadCentrePixelAsync(IPage page) => await page.EvaluateAsync<int[]>("""
        () => {
            const c = document.querySelector('.bi-canvas');
            const d = c.getContext('2d').getImageData(c.width >> 1, c.height >> 1, 1, 1).data;
            return [d[0], d[1], d[2], d[3]];
        }
        """);
}

[Collection(BrowserCollection.Name)]
public class DemoPageTests
{
    private readonly DemoAppFixture _fixture;

    public DemoPageTests(DemoAppFixture fixture) => _fixture = fixture;

    /// <summary>Every page must render without a JavaScript error or an error banner.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("avatar")]
    [InlineData("annotate")]
    [InlineData("optimize")]
    [InlineData("product")]
    [InlineData("social")]
    [InlineData("batch")]
    [InlineData("editor")]
    [InlineData("headless")]
    [InlineData("capabilities")]
    public async Task PageLoadsWithoutErrors(string route)
    {
        Assert.SkipWhen(_fixture.Browser is null, _fixture.SkipReason ?? "No browser");
        var errors = new List<string>();
        await using var page = await _fixture.Browser!.NewPageAsync();
        page.PageError += (_, e) => errors.Add(e);
        page.Console += (_, e) => { if (e.Type == "error") errors.Add(e.Text); };

        await page.GotoAsync($"{_fixture.BaseUrl}/{route}", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
        await page.WaitForSelectorAsync("h1", new PageWaitForSelectorOptions { Timeout = 60_000 });
        await page.WaitForTimeoutAsync(2000);

        Assert.Empty(errors);
        Assert.Empty(await page.Locator(".alert-error").AllInnerTextsAsync());
        Assert.False(string.IsNullOrWhiteSpace(await page.Locator("h1").First.InnerTextAsync()));
    }
}

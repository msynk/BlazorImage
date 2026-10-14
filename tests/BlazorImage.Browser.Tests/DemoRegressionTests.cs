using Microsoft.Playwright;
using Xunit;

namespace BlazorImage.Browser.Tests;

/// <summary>
/// Regression tests for defects found by driving every demo page in a browser: overlays drifting off the image,
/// pointer input lost over the crop handles, undo history, paste, and page state that only shows up in a real DOM.
/// </summary>
[Collection(BrowserCollection.Name)]
public class DemoRegressionTests
{
    private readonly DemoAppFixture _fixture;

    public DemoRegressionTests(DemoAppFixture fixture) => _fixture = fixture;

    private async Task<IPage> OpenEditorWithImageAsync()
    {
        var page = await _fixture.OpenAsync("editor");
        var file = _fixture.WriteSampleImage();
        try
        {
            await page.SetInputFilesAsync(".bi-editor input[type=file]", file);
            await page.WaitForFunctionAsync("() => document.querySelector('.bi-canvas')?.width > 1", null,
                new PageWaitForFunctionOptions { Timeout = 60_000 });
            await page.WaitForTimeoutAsync(1000);
        }
        finally
        {
            File.Delete(file);
        }
        return page;
    }

    private static async Task DragAsync(IPage page, float x0, float y0, float x1, float y1)
    {
        await page.Mouse.MoveAsync(x0, y0);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(x1, y1, new MouseMoveOptions { Steps = 8 });
        await page.Mouse.UpAsync();
        await page.WaitForTimeoutAsync(800);
    }

    [Fact]
    public async Task TheImageIsFittedInsideTheEditingArea()
    {
        await using var page = await OpenEditorWithImageAsync();
        var canvas = (await page.Locator(".bi-canvas").BoundingBoxAsync())!;
        var host = (await page.Locator(".bi-canvas-host").BoundingBoxAsync())!;
        Assert.InRange(canvas.X, host.X, host.X + host.Width);
        Assert.InRange(canvas.Y, host.Y, host.Y + host.Height);
        Assert.True(canvas.X + canvas.Width <= host.X + host.Width + 1 && canvas.Y + canvas.Height <= host.Y + host.Height + 1,
            $"the image ({canvas.Width}x{canvas.Height}) overflows the editing area ({host.Width}x{host.Height})");
    }

    [Fact]
    public async Task TheCropFrameFollowsTheDragAndItsHandles()
    {
        await using var page = await OpenEditorWithImageAsync();
        await page.Locator(".bi-tool[title=Crop]").ClickAsync();
        var canvas = (await page.Locator(".bi-canvas").BoundingBoxAsync())!;
        var x = canvas.X + canvas.Width * 0.2f;
        var y = canvas.Y + canvas.Height * 0.2f;
        await DragAsync(page, x, y, x + 200, y + 120);

        var frame = (await page.Locator(".bi-crop-frame").BoundingBoxAsync())!;
        Assert.InRange(frame.X, x - 2, x + 2);
        Assert.InRange(frame.Y, y - 2, y + 2);
        Assert.InRange(frame.Width, 198, 202);

        // Shrinking from a handle moves the pointer over the frame; the drag must keep tracking it.
        var handle = (await page.Locator(".bi-handle-bottomright").BoundingBoxAsync())!;
        await DragAsync(page, handle.X + handle.Width / 2, handle.Y + handle.Height / 2,
            handle.X + handle.Width / 2 - 60, handle.Y + handle.Height / 2 - 30);
        frame = (await page.Locator(".bi-crop-frame").BoundingBoxAsync())!;
        Assert.InRange(frame.Width, 137, 143);
        Assert.InRange(frame.Height, 87, 93);

        // And the next drag still works: a release over the frame must not leave the pointer stuck.
        await DragAsync(page, frame.X + frame.Width / 2, frame.Y + frame.Height / 2,
            frame.X + frame.Width / 2 + 40, frame.Y + frame.Height / 2 + 20);
        var moved = (await page.Locator(".bi-crop-frame").BoundingBoxAsync())!;
        Assert.InRange(moved.X - frame.X, 38, 42);
    }

    [Fact]
    public async Task ThePanToolMovesTheImageWithThePointer()
    {
        await using var page = await OpenEditorWithImageAsync();
        await page.Locator(".bi-tool[title=Pan]").ClickAsync();
        var before = (await page.Locator(".bi-canvas").BoundingBoxAsync())!;
        await DragAsync(page, before.X + 100, before.Y + 100, before.X + 200, before.Y + 150);
        var after = (await page.Locator(".bi-canvas").BoundingBoxAsync())!;
        Assert.InRange(after.X - before.X, 98, 102);
        Assert.InRange(after.Y - before.Y, 48, 52);
    }

    [Fact]
    public async Task ShapesAndTheirSelectionLandWhereTheyWereDrawn()
    {
        await using var page = await OpenEditorWithImageAsync();
        await page.Locator(".bi-tool[title=Rectangle]").ClickAsync();
        var canvas = (await page.Locator(".bi-canvas").BoundingBoxAsync())!;
        var x0 = canvas.X + canvas.Width * 0.25f;
        var y0 = canvas.Y + canvas.Height * 0.25f;
        await DragAsync(page, x0, y0, canvas.X + canvas.Width * 0.75f, canvas.Y + canvas.Height * 0.75f);

        // The document is in image pixels: the 800x600 sample, so a 25%..75% drag is (200,150) 400x300.
        var document = await page.Locator("pre.code").First.InnerTextAsync();
        Assert.Contains("at (200,150) 400×300", document, StringComparison.Ordinal);

        await page.Locator(".bi-tool[title=Select]").ClickAsync();
        await page.Mouse.ClickAsync(x0 + 1, canvas.Y + canvas.Height * 0.5f);
        await page.WaitForTimeoutAsync(500);
        var selection = (await page.Locator(".bi-selection").BoundingBoxAsync())!;
        Assert.InRange(selection.X, x0 - 3, x0 + 3);
        Assert.InRange(selection.Y, y0 - 3, y0 + 3);
    }

    [Fact]
    public async Task OneUndoReversesMovingAnAnnotation()
    {
        await using var page = await OpenEditorWithImageAsync();
        await page.Locator(".bi-tool[title=Rectangle]").ClickAsync();
        var canvas = (await page.Locator(".bi-canvas").BoundingBoxAsync())!;
        var x0 = canvas.X + canvas.Width * 0.25f;
        var ym = canvas.Y + canvas.Height * 0.5f;
        await DragAsync(page, x0, canvas.Y + canvas.Height * 0.25f, canvas.X + canvas.Width * 0.75f, canvas.Y + canvas.Height * 0.75f);
        var drawn = (await page.Locator("pre.code").First.InnerTextAsync()).Split('\n').Last();

        await page.Locator(".bi-tool[title=Select]").ClickAsync();
        await DragAsync(page, x0 + 1, ym, x0 + 61, ym + 30);
        var moved = (await page.Locator("pre.code").First.InnerTextAsync()).Split('\n').Last();
        Assert.NotEqual(drawn, moved);

        await page.Locator(".bi-canvas-host").FocusAsync();
        await page.Keyboard.PressAsync("Control+z");
        await page.WaitForTimeoutAsync(1200);
        Assert.Equal(drawn, (await page.Locator("pre.code").First.InnerTextAsync()).Split('\n').Last());
    }

    [Fact]
    public async Task ChoosingAHistoryEntryShowsThatState()
    {
        await using var page = await OpenEditorWithImageAsync();
        await page.Locator(".bi-tool[title=Rectangle]").ClickAsync();
        var canvas = (await page.Locator(".bi-canvas").BoundingBoxAsync())!;
        await DragAsync(page, canvas.X + 50, canvas.Y + 50, canvas.X + 150, canvas.Y + 120);
        await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "History" }).ClickAsync();
        await page.Locator(".bi-history-button").First.ClickAsync();
        await page.WaitForTimeoutAsync(1200);
        Assert.Contains("Annotations (0)", await page.Locator("pre.code").First.InnerTextAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PastingAnImageLoadsItIntoTheAnnotationEditor()
    {
        await using var page = await _fixture.OpenAsync("annotate");
        await page.WaitForTimeoutAsync(1000);
        await page.EvaluateAsync("""
            (b64) => {
                const bytes = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
                const data = new DataTransfer();
                data.items.add(new File([bytes], 'screenshot.png', { type: 'image/png' }));
                document.body.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
            }
            """, Convert.ToBase64String(_fixture.SampleImage));
        await page.WaitForFunctionAsync("() => document.querySelector('.bi-canvas')?.width === 800", null,
            new PageWaitForFunctionOptions { Timeout = 60_000 });

        // The page keeps passing the pasted image to the editor; re-rendering must not reload it and lose the edit.
        await page.Locator(".bi-tool[title=Rectangle]").ClickAsync();
        var canvas = (await page.Locator(".bi-canvas").BoundingBoxAsync())!;
        await DragAsync(page, canvas.X + 40, canvas.Y + 40, canvas.X + 140, canvas.Y + 110);
        await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Layers" }).ClickAsync();
        await page.WaitForTimeoutAsync(800);
        Assert.Contains("Rectangle", await page.Locator(".bi-layer-name").AllInnerTextsAsync());
    }

    [Fact]
    public async Task OptimisingReportsFilesThatFailEvenWhenAllOfThemDo()
    {
        await using var page = await _fixture.OpenAsync("optimize");
        var path = Path.Combine(Path.GetTempPath(), $"blazorimage-{Guid.NewGuid():N}-notes.txt");
        await File.WriteAllTextAsync(path, "not an image");
        try
        {
            await page.SetInputFilesAsync("input[type=file]", path);
            await page.WaitForSelectorAsync("table.data", new PageWaitForSelectorOptions { Timeout = 60_000 });
            Assert.Contains("Not a readable image", await page.Locator("table.data").InnerTextAsync(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ToggleButtonsExposeTheirStateToAssistiveTechnology()
    {
        await using var page = await _fixture.OpenAsync("product");
        Assert.Equal("true", await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "512", Exact = true }).GetAttributeAsync("aria-pressed"));
        Assert.Equal("false", await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "2048", Exact = true }).GetAttributeAsync("aria-pressed"));
    }

    [Fact]
    public async Task TheSampleImageRunsEveryHeadlessExample()
    {
        await using var page = await _fixture.OpenAsync("headless");
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Use a sample image" }).ClickAsync();
        await page.WaitForFunctionAsync("() => document.querySelectorAll('.grid .card img').length === 7 && !document.querySelector('.working')", null,
            new PageWaitForFunctionOptions { Timeout = 180_000 });
        Assert.Equal(7, await page.Locator(".grid .card").CountAsync());
    }
}

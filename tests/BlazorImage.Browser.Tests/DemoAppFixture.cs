using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Playwright;
using Xunit;

namespace BlazorImage.Browser.Tests;

/// <summary>
/// Starts the demo application once and drives a real browser against it, so these tests cover what unit tests cannot:
/// JavaScript interop, the browser's own image codecs, canvas rendering and pointer input.
/// </summary>
/// <remarks>
/// Playwright's bundled browsers are used when installed. When they are not, the fixture falls back to a Chrome or Edge
/// installed on the machine, and skips the whole class if neither is available, so a checkout without browsers still
/// builds and runs the rest of the suite.
/// </remarks>
public sealed class DemoAppFixture : IAsyncLifetime
{
    private Process? _server;
    private IPlaywright? _playwright;

    /// <summary>The running browser, or null when no browser could be found.</summary>
    public IBrowser? Browser { get; private set; }

    /// <summary>Base address of the demo application.</summary>
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>Why the browser is unavailable, for a clear skip message.</summary>
    public string? SkipReason { get; private set; }

    /// <summary>A deterministic PNG used as the upload in every test.</summary>
    public byte[] SampleImage { get; } = CreateSampleImage(800, 600);

    public async ValueTask InitializeAsync()
    {
        try
        {
            _playwright = await Playwright.CreateAsync();
        }
        catch (Exception ex)
        {
            SkipReason = $"Playwright could not start: {ex.Message}";
            return;
        }

        Browser = await LaunchBrowserAsync(_playwright);
        if (Browser is null)
        {
            SkipReason = "No browser is available. Run 'pwsh bin/Debug/net10.0/playwright.ps1 install chromium', or install Chrome or Edge.";
            return;
        }

        var port = GetFreePort();
        BaseUrl = $"http://localhost:{port}";
        var failure = await StartServerAsync(port);
        if (failure is not null)
        {
            SkipReason = $"The demo application did not start: {failure}";
            await Browser.DisposeAsync();
            Browser = null;
        }
    }

    private static async Task<IBrowser?> LaunchBrowserAsync(IPlaywright playwright)
    {
        // Prefer Playwright's own build, then a system Chrome, then Edge.
        foreach (var options in new[]
                 {
                     new BrowserTypeLaunchOptions { Headless = true },
                     new BrowserTypeLaunchOptions { Headless = true, Channel = "chrome" },
                     new BrowserTypeLaunchOptions { Headless = true, Channel = "msedge" },
                 })
        {
            try
            {
                return await playwright.Chromium.LaunchAsync(options);
            }
            catch (PlaywrightException)
            {
                // Try the next option.
            }
        }
        return null;
    }

    /// <summary>Starts the demo host. Returns null on success, or a description of what went wrong.</summary>
    private async Task<string?> StartServerAsync(int port)
    {
        var root = FindRepositoryRoot();
        if (root is null) return "the repository root was not found";
        var project = Path.Combine(root, "samples", "BlazorImage.Demo", "BlazorImage.Demo.csproj");
        if (!File.Exists(project)) return $"'{project}' does not exist";

        _server = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet", $"run --project \"{project}\" --urls {BaseUrl}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        _server.Start();
        var stdout = _server.StandardOutput.ReadToEndAsync();
        var stderr = _server.StandardError.ReadToEndAsync();

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            if (_server.HasExited)
            {
                var output = (await stdout) + (await stderr);
                return $"the host exited with code {_server.ExitCode}. {Tail(output)}";
            }
            try
            {
                // The framework files are the real readiness signal; the index page serves before them.
                using var response = await client.GetAsync($"{BaseUrl}/_framework/dotnet.js");
                if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength > 1000) return null;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(500);
        }
        return "it did not become ready within three minutes";
    }

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 600 ? trimmed : "..." + trimmed[^600..];
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            // The solution may be a classic .sln or the newer .slnx.
            if (File.Exists(Path.Combine(directory.FullName, "BlazorImage.slnx")) ||
                File.Exists(Path.Combine(directory.FullName, "BlazorImage.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Opens a page at the given route and waits for the application to become interactive.</summary>
    public async Task<IPage> OpenAsync(string route)
    {
        Assert.SkipWhen(Browser is null, SkipReason ?? "No browser");
        var page = await Browser!.NewPageAsync(new BrowserNewPageOptions { ViewportSize = new ViewportSize { Width = 1400, Height = 900 } });
        await page.GotoAsync($"{BaseUrl}/{route}", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
        await page.WaitForSelectorAsync("h1", new PageWaitForSelectorOptions { Timeout = 60_000 });
        return page;
    }

    /// <summary>Writes the sample image to a temporary file for upload, returning its path.</summary>
    public string WriteSampleImage(string name = "sample.png")
    {
        var path = Path.Combine(Path.GetTempPath(), $"blazorimage-{Guid.NewGuid():N}-{name}");
        File.WriteAllBytes(path, SampleImage);
        return path;
    }

    /// <summary>Builds a deterministic gradient PNG using this library's own encoder.</summary>
    private static byte[] CreateSampleImage(int width, int height)
    {
        using var image = ImageBuffer.Create(width, height, clear: false);
        for (var y = 0; y < height; y++)
        {
            var row = image.GetRow(y);
            for (var x = 0; x < width; x++)
            {
                row[x] = new Rgba32(
                    (byte)(x * 255 / Math.Max(1, width - 1)),
                    (byte)(y * 255 / Math.Max(1, height - 1)),
                    (byte)(((x >> 5) + (y >> 5)) % 2 == 0 ? 40 : 220));
            }
        }
        return Codecs.Png.PngEncoder.Encode(image).Data;
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null) await Browser.DisposeAsync();
        _playwright?.Dispose();
        if (_server is { HasExited: false })
        {
            try
            {
                _server.Kill(entireProcessTree: true);
                await _server.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
            }
            catch (Exception) { /* the process is going away regardless */ }
        }
        _server?.Dispose();
    }
}

/// <summary>Shares one demo application and browser across every browser test.</summary>
[CollectionDefinition(Name)]
public sealed class BrowserCollection : ICollectionFixture<DemoAppFixture>
{
    public const string Name = "browser";
}

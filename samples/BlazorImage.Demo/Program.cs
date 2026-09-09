using BlazorImage;
using BlazorImage.Demo;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// One call wires up capability detection, loading, processing and export.
builder.Services.AddBlazorImage(options =>
{
    // Generous for a demo; a real app would tune these to its users' devices.
    options.MaxSourceBytes = 64 * 1024 * 1024;
    options.Limits = ImageLimits.Default with { MaxPixels = 80_000_000 };
});

await builder.Build().RunAsync();

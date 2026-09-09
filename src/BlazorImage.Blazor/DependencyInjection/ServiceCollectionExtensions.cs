using BlazorImage.Capabilities;
using BlazorImage.Input;
using BlazorImage.Interop;
using BlazorImage.Output;
using BlazorImage.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BlazorImage;

/// <summary>Registers BlazorImage with the dependency injection container.</summary>
public static class BlazorImageServiceCollectionExtensions
{
    /// <summary>
    /// Adds the BlazorImage services: capability detection, image loading, processing and export.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Services are registered as scoped, which is correct for both hosting models: in WebAssembly a scope lives for the
    /// whole application, and in Server one scope exists per circuit, so browser state is never shared between users.
    /// </para>
    /// <para>
    /// In a Blazor Web App, register this in the client project for WebAssembly and Auto render modes, and in the server
    /// project as well if any component uses Interactive Server.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddBlazorImage(options =>
    /// {
    ///     options.Limits = ImageLimits.Default with { MaxPixels = 40_000_000 };
    ///     options.MaxSourceBytes = 32 * 1024 * 1024;
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddBlazorImage(this IServiceCollection services, Action<BlazorImageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new BlazorImageOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        services.TryAddScoped<BrowserImageInterop>();
        services.TryAddScoped<BrowserCapabilityProvider>();
        services.TryAddScoped<IImageCapabilityProvider>(sp => sp.GetRequiredService<BrowserCapabilityProvider>());
        services.TryAddScoped(sp => new ImageProcessorFactory(
            sp.GetRequiredService<BrowserImageInterop>(),
            sp.GetRequiredService<IImageCapabilityProvider>(),
            sp.GetRequiredService<BlazorImageOptions>()));
        services.TryAddScoped(sp =>
        {
            var loader = new ImageLoader(
                sp.GetRequiredService<ImageProcessorFactory>(),
                sp.GetRequiredService<BrowserImageInterop>(),
                sp.GetRequiredService<IImageCapabilityProvider>());
            loader.MaxSourceBytes = sp.GetRequiredService<BlazorImageOptions>().MaxSourceBytes;
            return loader;
        });
        services.TryAddScoped<ImageExporter>();
        services.TryAddScoped<BrowserTextRasterizer>();
        services.TryAddScoped<FontTextRasterizer>();
        return services;
    }

    /// <summary>
    /// Adds BlazorImage without any browser integration, for server-side or console use. Only the managed codecs
    /// (PNG, JPEG, BMP and GIF decoding) are available.
    /// </summary>
    public static IServiceCollection AddBlazorImageHeadless(this IServiceCollection services, Action<BlazorImageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new BlazorImageOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<IImageCapabilityProvider>(ManagedCapabilityProvider.Instance);
        services.TryAddSingleton(sp =>
        {
            var o = sp.GetRequiredService<BlazorImageOptions>();
            return new Pipeline.ImageProcessor(Codecs.ImageCodecRegistry.CreateDefault(), o.Allocator, o.Limits);
        });
        services.TryAddSingleton<FontTextRasterizer>();
        return services;
    }
}

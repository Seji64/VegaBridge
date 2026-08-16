using System.Globalization;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using MudBlazor;
using MudBlazor.Services;
using MudExtensions.Services;
using CommunityToolkit.Maui;
using Polly;
using Serilog;
using Serilog.Events;
using Shiny.BluetoothLE;
using VegaBridgeApp.Services.BLE;
using VegaBridgeApp.Services.BLE.Plugins;
using VegaBridgeApp.Services.Closures;
using VegaBridgeApp.Services.Geocoding;
using VegaBridgeApp.Services.Location;
using VegaBridgeApp.Services.Navigation;
using VegaBridgeApp.Services.Routes;
using VegaBridgeApp.Services.Debug;
using VegaBridgeApp.Services.Valhalla;

namespace VegaBridgeApp;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        // Use Network.framework instead of legacy Security.framework C API.
        // The old AppleCrypto PAL cannot handle TLS 1.3 (valhalla1.openstreetmap.de
        // supports ONLY TLS 1.3). Network.framework is the modern Apple stack
        // used by Safari & Co. (dotnet/runtime#1979)
        if (OperatingSystem.IsMacOS())
        {
            AppContext.SetSwitch("System.Net.Security.UseNetworkFramework", true);
        }

        MauiAppBuilder builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseShiny()
            .UseMauiCommunityToolkit()
            .ConfigureFonts(fonts => { fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular"); fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold"); });

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddMudServices(config =>
        {
            config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomCenter;
        });
        builder.Services.AddMudExtensions();

        // ── Localization ────────────────────────────────────────────────────
        builder.Services.AddLocalization();

        // Detect system language (de vs. en)
        string systemLang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        string culture = systemLang == "de" ? "de" : "en";
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo(culture);
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo(culture);

        // ── BLE (Shiny.BluetoothLE) ────────────────────────────────────────
        builder.Services.AddSingleton(TimeProvider.System);
#if IOS || MACCATALYST
        // CoreBluetooth State Restoration: iOS can wake the app for BLE
        // events after it was suspended (screen off, phone in the pocket)
        // instead of silently dropping the connection to the bike. The
        // RestoreIdentifier is what enables that on iOS/macOS.
        builder.Services.AddBluetoothLE<VegaBridgeBleDelegate>(new AppleBleConfiguration(
            ShowPowerAlert: true,
            RestoreIdentifier: "vegabridge-ble"));
#else
        // VegaBridgeBleDelegate exists only on Apple targets (BleDelegate
        // is Apple-only) – other platforms use the plain registration.
        builder.Services.AddBluetoothLE();
#endif
        
        builder.Services.AddSingleton<BleManagerService>();
        builder.Services.AddTransient<IBleDevicePlugin, MvAgustaBlePlugin>();

        // ── GPS / Location (Shiny.Locations) ───────────────────────────────
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddShinyStores();
        builder.Services.AddGps<GpsDelegate>();
        builder.Services.AddSingleton<GpsService>();
        // ── Serilog: structured console logging ──────────────────────────
        // Console output is not reliably visible in MAUI, so every line also
        // goes to the in-memory DebugLogSink (exportable from the Settings
        // page). The sink itself only buffers while IsEnabled is true
        // (toggled in Settings), so normal use costs nothing.
        builder.Services.AddSingleton(DebugLogSink.Instance);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Sink(DebugLogSink.Instance)
            .CreateLogger();

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(dispose: true);
        
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        // ── Valhalla routing: named HttpClient with resilience pipeline ──────
        builder.Services.AddHttpClient(ValhallaOptions.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(ValhallaOptions.DefaultBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(ValhallaOptions.TimeoutSeconds);
        })
        .ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler
        {
            // Force TLS 1.3 (the server only supports TLS 1.3)
            SslProtocols = System.Security.Authentication.SslProtocols.Tls13
        })
        .AddResilienceHandler("valhalla-retry", static builder =>
        {
            // Retry on transient HTTP failures (timeout, 5xx, HttpRequestException)
            builder.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = ValhallaOptions.RetryCount,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
            });
        });

        builder.Services.AddSingleton<IValhallaClient, ValhallaClient>();

        // ── Photon Geocoding (Autocomplete) ─────────────────────────────────
        builder.Services.AddHttpClient(GeocodingService.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://photon.komoot.io");
            client.Timeout = TimeSpan.FromSeconds(5);
        })
        .ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler
        {
            SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
        });
        builder.Services.AddSingleton<IGeocodingService, GeocodingService>();

        // ── Road Closure Check (provider-based) ────────────────────────────
        // Multiple providers can be enabled in Settings (Overpass/OSM,
        // MobiData BW official roadworks, …). Same resilience pattern as the
        // Valhalla client: named HttpClient + Polly retry with exponential
        // backoff + jitter.
        builder.Services.AddHttpClient(OverpassRoadClosureProvider.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://overpass-api.de/");
            client.Timeout = TimeSpan.FromSeconds(30);
            // Overpass rejects requests without a (non-browser) User-Agent
            // with HTTP 406 – Flurl/HttpClient sends none by default.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("VegaBridge/1.0 (closure-check)");
        })
        .AddResilienceHandler("overpass-retry", static builder =>
        {
            builder.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = OverpassRoadClosureProvider.RetryCount,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                // A 504 from Overpass means the query was too heavy for the
                // server (timeout) – retrying the SAME query will just get
                // another 504. Fail fast so the provider timeout in the
                // aggregator can surface the other providers' results.
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .HandleResult(r => r.StatusCode != System.Net.HttpStatusCode.GatewayTimeout)
            });
        });

        builder.Services.AddHttpClient(MobiDataRoadClosureProvider.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.mobidata-bw.de/");
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("VegaBridge/1.0 (closure-check)");
        })
        .AddResilienceHandler("mobidata-retry", static builder =>
        {
            builder.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = MobiDataRoadClosureProvider.RetryCount,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
            });
        });

        builder.Services.AddSingleton<IRoadClosureProvider, OverpassRoadClosureProvider>();
        builder.Services.AddSingleton<IRoadClosureProvider, MobiDataRoadClosureProvider>();
        // Register the concrete type first, then map the interface onto the
        // SAME singleton instance – the Settings page injects the concrete
        // type (for the provider list + persistence), the Map injects the
        // interface. Two separate registrations would create two instances
        // and the provider selection would not be shared.
        builder.Services.AddSingleton<RoadClosureService>();
        builder.Services.AddSingleton<IRoadClosureService>(
            sp => sp.GetRequiredService<RoadClosureService>());

        // ── Route Persistence & GPX Conversion ───────────────────────────────
        builder.Services.AddSingleton<IRouteStorageService, RouteStorageService>();
        builder.Services.AddSingleton<IGpxService, GpxService>();

        // ── Navigation State Machine ─────────────────────────────────────────
        builder.Services.AddSingleton<NavigationService>();

        // ── BLE ↔ Navigation bridge ───────────────────────────────────────────
        // BleNavigationCoordinator subscribes to NavigationService events in its
        // constructor and translates them into BLE frames via BleManagerService.
        // It MUST be resolved once at startup, otherwise no navigation frames
        // (DEST/REM/NAVI/SM/SM1/FINISH) are ever sent during real route navigation.
        builder.Services.AddSingleton<BleNavigationCoordinator>();

        MauiApp app = builder.Build();

        // Force-instantiate the coordinator now (singleton) so it subscribes
        // to navigation events for the app's lifetime.
        _ = app.Services.GetRequiredService<BleNavigationCoordinator>();

        return app;
    }
}

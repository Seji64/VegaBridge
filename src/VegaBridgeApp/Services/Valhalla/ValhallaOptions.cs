namespace VegaBridgeApp.Services.Valhalla;

/// <summary>
/// Static configuration constants for the Valhalla routing service.
/// The base URL is compiled in – no runtime configuration needed.
/// If self‑hosting, change the constant here.
/// </summary>
public static class ValhallaOptions
{
    /// <summary>
    /// Default public Valhalla demo server (no API key required).
    /// </summary>
    public const string DefaultBaseUrl = "https://valhalla1.openstreetmap.de";

    /// <summary>
    /// Request timeout in seconds, all retry attempts together.
    /// </summary>
    public const int TimeoutSeconds = 30;

    /// <summary>
    /// Timeout of a single attempt in seconds (the public server needs ≈ 2 s
    /// even for 2000 km avoiding highways). Lets a hung attempt be retried
    /// within <see cref="TimeoutSeconds"/>.
    /// </summary>
    public const int AttemptTimeoutSeconds = 10;

    /// <summary>
    /// Maximum number of retry attempts on transient failures.
    /// </summary>
    public const int RetryCount = 2;

    /// <summary>
    /// Named HttpClient key used for DI registration.
    /// </summary>
    public const string HttpClientName = "Valhalla";
}

namespace MyRedis.Abstractions.Configuration;

/// <summary>
/// Main configuration service interface - thread-safe, observable, production-ready.
///
/// Design Philosophy:
/// - Simple API for 99% use cases (Get/Set)
/// - Type-safe access with compile-time guarantees
/// - Runtime validation with friendly errors
/// - Observable for monitoring and debugging
///
/// Thread Safety:
/// - All methods are thread-safe
/// - Single-threaded event loop means no contention
///
/// Example Usage:
/// // Simple access
/// var threshold = configService.Get&lt;int&gt;("lazyfree-lazy-server-del");
///
/// // Set with validation
/// var result = await configService.SetAsync("maxclients", "10000");
/// if (!result.Success)
///     Console.WriteLine($"Error: {result.ErrorMessage}");
/// </summary>
public interface IConfigurationService
{
    /// <summary>
    /// Gets a configuration parameter value with type safety.
    /// Returns default value if parameter doesn't exist.
    /// Throws if type mismatch (fail-fast principle).
    /// </summary>
    T Get<T>(string name);

    /// <summary>
    /// Gets parameter value as string (universal accessor).
    /// Useful for CONFIG GET command implementation.
    /// </summary>
    string GetString(string name);

    /// <summary>
    /// Sets a configuration parameter with validation.
    /// Returns result with success/error information.
    /// Triggers change notifications if successful.
    /// Logs change with timestamp and metadata.
    /// </summary>
    Task<ConfigResult> SetAsync(string name, string value);

    /// <summary>
    /// Gets all parameters (for CONFIG GET * implementation).
    /// Returns dictionary of name -> current value.
    /// </summary>
    IReadOnlyDictionary<string, string> GetAll();

    /// <summary>
    /// Gets all parameters matching a pattern (glob-style).
    /// Example: GetMatching("max*") returns maxclients, maxmemory, etc.
    /// </summary>
    IReadOnlyDictionary<string, string> GetMatching(string pattern);

    /// <summary>
    /// Gets parameter metadata for introspection.
    /// Useful for CONFIG GET with type information.
    /// </summary>
    IConfigParameter? GetParameterMetadata(string name);

    /// <summary>
    /// Subscribes to configuration changes (observer pattern).
    /// Callback invoked when any parameter changes.
    /// Returns IDisposable for unsubscribe.
    /// </summary>
    IDisposable Subscribe(Action<ConfigChangeEvent> callback);

    /// <summary>
    /// Subscribes to specific parameter changes.
    /// More efficient than global subscription.
    /// </summary>
    IDisposable Subscribe(string name, Action<ConfigChangeEvent> callback);
}

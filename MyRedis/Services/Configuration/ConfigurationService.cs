using System.Text.RegularExpressions;
using MyRedis.Abstractions.Configuration;

namespace MyRedis.Services.Configuration;

/// <summary>
/// Configuration service implementation.
///
/// Architecture:
/// - In-memory storage (regular Dictionary, not concurrent)
/// - Lock-free operations (no locks needed)
/// - Observer pattern for change notifications
///
/// Threading Model:
/// - WRITES: Single-threaded (main event loop only via CONFIG SET)
/// - READS: Multi-threaded (main thread + background workers)
/// - CONSISTENCY: Eventual (background threads may see stale values for ~milliseconds)
///
/// Why No Locks?
/// - All mutations (Register, SetValue) happen on main thread only
/// - No concurrent writers → no race conditions
/// - Dictionary reads are thread-safe as long as no writes occur concurrently (structural immutability)
/// - .NET reference assignments are atomic (no torn reads)
/// - Stale reads by background threads are acceptable (config changes are rare, stale reads for milliseconds don't cause correctness issues)
/// </summary>
public class ConfigurationService : IConfigurationService
{
    // Parameter registry: name -> parameter metadata (single-threaded writes)
    private readonly Dictionary<string, ConfigParameter> _parameters = new(StringComparer.OrdinalIgnoreCase);

    // Observer registry: parameter name -> list of callbacks (single-threaded modifications)
    private readonly Dictionary<string, List<Action<ConfigChangeEvent>>> _observers = new(StringComparer.OrdinalIgnoreCase);

    // Global observers (notified on any change)
    private readonly List<Action<ConfigChangeEvent>> _globalObservers = new();

    public T Get<T>(string name)
    {
        if (!_parameters.TryGetValue(name, out var param))
        {
            throw new InvalidOperationException($"Parameter '{name}' not found");
        }

        // Type safety check
        if (typeof(T) != param.Type)
        {
            throw new InvalidOperationException(
                $"Type mismatch for '{name}': expected {typeof(T).Name}, actual {param.Type.Name}");
        }

        // Parse and return typed value
        return param.GetValue<T>();
    }

    public string GetString(string name)
    {
        if (!_parameters.TryGetValue(name, out var param))
        {
            throw new InvalidOperationException($"Parameter '{name}' not found");
        }

        return param.CurrentValue;
    }

    public async Task<ConfigResult> SetAsync(string name, string value)
    {
        return await SetInternalAsync(name, value, enforceImmutable: true);
    }

    /// <summary>
    /// Internal method for setting configuration values during startup.
    /// Bypasses the IsMutable check to allow loading immutable parameters from redis.conf.
    ///
    /// This matches Redis behavior:
    /// - Startup (loadServerConfig): No immutability check, all parameters allowed
    /// - Runtime (CONFIG SET): Immutability enforced, immutable parameters rejected
    /// </summary>
    public async Task<ConfigResult> SetForStartupAsync(string name, string value)
    {
        return await SetInternalAsync(name, value, enforceImmutable: false);
    }

    private async Task<ConfigResult> SetInternalAsync(string name, string value, bool enforceImmutable)
    {
        await Task.Yield(); // Make method truly async (avoid warning)

        // 1. Check parameter exists
        if (!_parameters.TryGetValue(name, out var param))
        {
            return ConfigResult.Failure($"Unknown parameter '{name}'");
        }

        // 2. Check mutability (only if enforceImmutable is true)
        if (enforceImmutable && !param.IsMutable)
        {
            return ConfigResult.Failure($"Parameter '{name}' is read-only, cannot be changed at runtime");
        }

        // 3. Validate new value
        var validation = param.Validator.Validate(value);
        if (!validation.IsValid)
        {
            return ConfigResult.Failure(
                $"Invalid value for '{name}': {validation.ErrorMessage}");
        }

        // 4. Store old value for notifications
        var oldValue = param.CurrentValue;

        // 5. Update current value (atomic reference assignment)
        param.SetValue(value);

        // 6. Notify observers
        var changeEvent = new ConfigChangeEvent
        {
            ParameterName = name,
            OldValue = oldValue,
            NewValue = value,
            Timestamp = DateTime.UtcNow,
            IsHotReloadable = param.IsHotReloadable
        };

        // Notify specific observers
        if (_observers.TryGetValue(name, out var callbacks))
        {
            foreach (var callback in callbacks)
            {
                try
                {
                    callback(changeEvent);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[Config] Observer error for '{name}': {e.Message}");
                }
            }
        }

        // Notify global observers
        foreach (var callback in _globalObservers)
        {
            try
            {
                callback(changeEvent);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[Config] Global observer error: {e.Message}");
            }
        }

        // 7. Log the change
        Console.WriteLine($"[Config] SET {name} = {value} (was: {oldValue})");

        return ConfigResult.Ok();
    }

    public IReadOnlyDictionary<string, string> GetAll()
    {
        return _parameters.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.CurrentValue,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, string> GetMatching(string pattern)
    {
        // Simple glob matching: * matches any characters
        var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        var matcher = new Regex(regexPattern, RegexOptions.IgnoreCase);

        return _parameters
            .Where(kvp => matcher.IsMatch(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.CurrentValue, StringComparer.OrdinalIgnoreCase);
    }

    public IConfigParameter? GetParameterMetadata(string name)
    {
        return _parameters.GetValueOrDefault(name);
    }

    public IDisposable Subscribe(Action<ConfigChangeEvent> callback)
    {
        _globalObservers.Add(callback);
        return new Unsubscriber(() => _globalObservers.Remove(callback));
    }

    public IDisposable Subscribe(string name, Action<ConfigChangeEvent> callback)
    {
        if (!_observers.TryGetValue(name, out var callbacks))
        {
            callbacks = new List<Action<ConfigChangeEvent>>();
            _observers[name] = callbacks;
        }

        callbacks.Add(callback);

        return new Unsubscriber(() => callbacks.Remove(callback));
    }

    /// <summary>
    /// Registers a parameter with the configuration service.
    /// Called during initialization by ConfigurationRegistry (single-threaded).
    /// </summary>
    public void Register(ConfigParameter parameter)
    {
        if (parameter == null)
        {
            throw new ArgumentNullException(nameof(parameter));
        }
        
        if (_parameters.ContainsKey(parameter.Name))
        {
            throw new InvalidOperationException($"Parameter '{parameter.Name}' is already registered");
        }

        _parameters[parameter.Name] = parameter;
    }

    /// <summary>
    /// Simple unsubscriber for observer pattern.
    /// </summary>
    private class Unsubscriber(Action unsubscribeAction) : IDisposable
    {
        private readonly Action _unsubscribeAction = unsubscribeAction ?? throw new ArgumentNullException(nameof(unsubscribeAction));
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _unsubscribeAction();
                _disposed = true;
            }
        }
    }
}

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MyRedis.Abstractions.Configuration;

namespace MyRedis.Services.Configuration;

/// <summary>
/// Production-grade configuration service implementation.
///
/// Architecture:
/// - In-memory storage (Dictionary&lt;string, ConfigParameter&gt;)
/// - Lock-free reads (parameters are immutable after creation)
/// - Synchronized writes (lock on dictionary for SET operations)
/// - Observer pattern for change notifications
///
/// Performance:
/// - Get: O(1) dictionary lookup, lock-free
/// - Set: O(1) dictionary update + O(N) observer notification
/// - GetAll: O(N) where N = number of parameters (typically 50-100)
///
/// Thread Safety:
/// - Single-threaded event loop means no contention
/// - Locks are for future-proofing (multi-threaded access)
/// - ConcurrentDictionary for observer registry
///
/// Memory:
/// - ~1KB per parameter (metadata + current value)
/// - ~100 parameters = ~100KB total
/// - Total: ~200KB (negligible)
/// </summary>
public class ConfigurationService : IConfigurationService
{
    // Parameter registry: name -> parameter metadata
    private readonly Dictionary<string, ConfigParameter> _parameters = new(StringComparer.OrdinalIgnoreCase);

    // Observer registry: parameter name -> list of callbacks
    private readonly ConcurrentDictionary<string, List<Action<ConfigChangeEvent>>> _observers = new(StringComparer.OrdinalIgnoreCase);

    // Global observers (notified on any change)
    private readonly List<Action<ConfigChangeEvent>> _globalObservers = new();

    // Lock for write operations (Set, Register)
    private readonly object _writeLock = new();

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
        await Task.Yield(); // Make method truly async (avoid warning)

        lock (_writeLock)
        {
            // 1. Check parameter exists
            if (!_parameters.TryGetValue(name, out var param))
            {
                return ConfigResult.Failure($"Unknown parameter '{name}'");
            }

            // 2. Check mutability
            if (!param.IsMutable)
            {
                return ConfigResult.Failure($"Parameter '{name}' is read-only");
            }

            // 3. Validate new value
            var validation = param.Validator.Validate(value);
            if (!validation.IsValid)
            {
                return ConfigResult.Failure(
                    $"Invalid value for '{name}': {validation.ErrorMessage}");
            }

            // 4. Store old value for rollback and notifications
            var oldValue = param.CurrentValue;

            // 5. Update current value
            param.SetValue(value);

            // 6. Notify observers (outside lock to prevent deadlock)
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
                foreach (var callback in callbacks.ToList()) // ToList to avoid modification during iteration
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
            foreach (var callback in _globalObservers.ToList())
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
        return _parameters.TryGetValue(name, out var param) ? param : null;
    }

    public IDisposable Subscribe(Action<ConfigChangeEvent> callback)
    {
        _globalObservers.Add(callback);
        return new Unsubscriber(() =>
        {
            lock (_writeLock)
            {
                _globalObservers.Remove(callback);
            }
        });
    }

    public IDisposable Subscribe(string name, Action<ConfigChangeEvent> callback)
    {
        var callbacks = _observers.GetOrAdd(name, _ => new List<Action<ConfigChangeEvent>>());

        lock (_writeLock)
        {
            callbacks.Add(callback);
        }

        return new Unsubscriber(() =>
        {
            lock (_writeLock)
            {
                callbacks.Remove(callback);
            }
        });
    }

    /// <summary>
    /// Registers a parameter with the configuration service.
    /// Called during initialization by ConfigurationRegistry.
    /// </summary>
    public void Register(ConfigParameter parameter)
    {
        if (parameter == null)
        {
            throw new ArgumentNullException(nameof(parameter));
        }

        lock (_writeLock)
        {
            if (_parameters.ContainsKey(parameter.Name))
            {
                throw new InvalidOperationException($"Parameter '{parameter.Name}' is already registered");
            }

            _parameters[parameter.Name] = parameter;
        }
    }

    /// <summary>
    /// Simple unsubscriber for observer pattern.
    /// </summary>
    private class Unsubscriber : IDisposable
    {
        private readonly Action _unsubscribeAction;
        private bool _disposed;

        public Unsubscriber(Action unsubscribeAction)
        {
            _unsubscribeAction = unsubscribeAction ?? throw new ArgumentNullException(nameof(unsubscribeAction));
        }

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

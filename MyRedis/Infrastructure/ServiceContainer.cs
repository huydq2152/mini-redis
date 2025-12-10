namespace MyRedis.Infrastructure;

/// <summary>
/// Simple dependency injection container
/// </summary>
public class ServiceContainer
{
    private readonly Dictionary<Type, object> _singletons = new();
    private readonly Dictionary<Type, Func<ServiceContainer, object>> _factories = new();

    /// <summary>
    /// Registers a singleton instance
    /// </summary>
    public void RegisterSingleton<T>(T instance) where T : class
    {
        _singletons[typeof(T)] = instance;
    }

    /// <summary>
    /// Registers a factory for creating instances
    /// </summary>
    public void RegisterFactory<T>(Func<ServiceContainer, T> factory) where T : class
    {
        _factories[typeof(T)] = container => factory(container);
    }

    /// <summary>
    /// Registers a singleton factory
    /// </summary>
    public void RegisterSingleton<T>(Func<ServiceContainer, T> factory) where T : class
    {
        _factories[typeof(T)] = container =>
        {
            if (!_singletons.TryGetValue(typeof(T), out var instance))
            {
                instance = factory(container);
                _singletons[typeof(T)] = instance;
            }
            return instance;
        };
    }

    /// <summary>
    /// Resolves a service instance
    /// </summary>
    public T Resolve<T>() where T : class
    {
        if (_singletons.TryGetValue(typeof(T), out var singleton))
        {
            return (T)singleton;
        }

        if (_factories.TryGetValue(typeof(T), out var factory))
        {
            return (T)factory(this);
        }

        throw new InvalidOperationException($"Service of type {typeof(T).Name} is not registered.");
    }

    /// <summary>
    /// Tries to resolve a service instance
    /// </summary>
    public T? TryResolve<T>() where T : class
    {
        try
        {
            return Resolve<T>();
        }
        catch
        {
            return null;
        }
    }
}
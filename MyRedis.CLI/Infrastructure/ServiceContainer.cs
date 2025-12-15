using MyRedis.CLI.Application;
using MyRedis.CLI.Domain;

namespace MyRedis.CLI.Infrastructure;

/// <summary>
/// Simple dependency injection container for the CLI application.
/// Implements the Service Locator pattern with manual registration.
/// </summary>
public sealed class ServiceContainer
{
    private readonly Dictionary<Type, object> _services = new();
    private readonly Dictionary<Type, Func<ServiceContainer, object>> _factories = new();

    /// <summary>
    /// Registers a singleton service instance.
    /// </summary>
    public void RegisterSingleton<T>(T instance) where T : class
    {
        _services[typeof(T)] = instance ?? throw new ArgumentNullException(nameof(instance));
    }

    /// <summary>
    /// Registers a factory function for creating service instances.
    /// </summary>
    public void RegisterFactory<T>(Func<ServiceContainer, T> factory) where T : class
    {
        _factories[typeof(T)] = container => factory(container);
    }

    /// <summary>
    /// Resolves a service instance.
    /// </summary>
    public T Resolve<T>() where T : class
    {
        var type = typeof(T);
        
        // Try to get existing instance first
        if (_services.TryGetValue(type, out var instance))
            return (T)instance;
            
        // Try to create using factory
        if (_factories.TryGetValue(type, out var factory))
        {
            var newInstance = (T)factory(this);
            _services[type] = newInstance; // Cache as singleton
            return newInstance;
        }
        
        throw new InvalidOperationException($"Service of type {typeof(T).Name} is not registered");
    }

    /// <summary>
    /// Configures all default services for the CLI application.
    /// </summary>
    public static ServiceContainer CreateDefault(RedisConnectionSettings? connectionSettings = null)
    {
        var container = new ServiceContainer();
        
        // Register domain services
        container.RegisterSingleton<ICommandParser>(new CommandParser());
        
        // Register factories for application services
        container.RegisterFactory<IFileCommandLoader>(c => 
            new FileCommandLoader(c.Resolve<ICommandParser>()));
            
        container.RegisterFactory<RedisClientService>(c => 
            new RedisClientService(connectionSettings));
            
        container.RegisterFactory<CliApplication>(c => 
            new CliApplication(
                c.Resolve<RedisClientService>(),
                c.Resolve<ICommandParser>(),
                c.Resolve<IFileCommandLoader>()));
        
        return container;
    }
}
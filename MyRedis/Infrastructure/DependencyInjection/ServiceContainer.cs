namespace MyRedis.Infrastructure.DependencyInjection;

/// <summary>
/// Simple dependency injection (DI) container for managing service lifetimes and dependencies.
///
/// Responsibility: Service Registration and Resolution
/// - Register services (by instance or factory)
/// - Resolve services (create or return cached instances)
/// - Manage singleton lifetimes
/// - Support dependency injection
///
/// Benefits:
/// - Loose coupling: Components depend on interfaces, not concrete classes
/// - Testability: Easy to inject mocks/fakes for testing
/// - Flexibility: Swap implementations without changing consumers
/// - Centralized configuration: All wiring in one place (RedisServerFactory)
///
/// Container Features:
/// - Singleton lifetime: One instance per service type
/// - Factory registration: Lazy creation with access to container
/// - Circular dependency detection: Would throw StackOverflowException
/// - Type-safe: Generics ensure compile-time type checking
///
/// Limitations (Simple Container):
/// - No transient/scoped lifetimes (only singleton)
/// - No automatic constructor injection (must use factories)
/// - No property injection
/// - No named registrations (one instance per type)
/// - No IDisposable support (no cleanup on dispose)
///
/// This is intentionally simple. For production, consider using a full-featured DI framework
///
/// Design Pattern: Service Locator + Factory Pattern
/// - Service Locator: Resolve() finds registered services
/// - Factory: Factories create instances with dependencies
/// </summary>
public class ServiceContainer
{
    // Stores singleton instances: Type -> instance
    // Once created, instances are cached here
    private readonly Dictionary<Type, object> _singletons = new();

    // Stores factory functions: Type -> factory
    // Factory is called to create instance (with access to container for dependencies)
    private readonly Dictionary<Type, Func<ServiceContainer, object>> _factories = new();

    /// <summary>
    /// Registers a singleton instance directly.
    ///
    /// Use this when you already have an instance and want to register it.
    ///
    /// The instance is stored immediately and will be returned whenever
    /// Resolve() is called.
    /// </summary>
    /// <typeparam name="T">Service type to register</typeparam>
    /// <param name="instance">Pre-created instance to register</param>
    public void RegisterSingleton<T>(T instance) where T : class
    {
        // Store instance in singletons dictionary
        // typeof(T) is the key, instance is the value
        _singletons[typeof(T)] = instance;
    }

    /// <summary>
    /// Registers a singleton factory (lazy singleton creation).
    ///
    /// This is the most common registration method in MyRedis.
    ///
    /// How It Works:
    /// 1. Factory is stored but NOT called yet (lazy)
    /// 2. On first Resolve():
    ///    a. Call factory to create instance
    ///    b. Cache instance in _singletons
    ///    c. Return instance
    /// 3. On subsequent Resolve():
    ///    a. Return cached instance (don't call factory again)
    ///
    /// Why Lazy?
    /// - Dependencies may not be registered yet
    /// - Factory can resolve dependencies from container
    /// - Instance created only when needed
    ///
    /// Dependency Graph Resolution:
    /// Container automatically resolves the entire dependency graph
    ///
    /// All resolved automatically with one call
    /// </summary>
    /// <typeparam name="T">Service type to register</typeparam>
    /// <param name="factory">Factory function that creates the instance</param>
    public void RegisterSingleton<T>(Func<ServiceContainer, T> factory) where T : class
    {
        // Store a factory that implements singleton pattern
        _factories[typeof(T)] = container =>
        {
            // Check if instance already exists (singleton check)
            if (!_singletons.TryGetValue(typeof(T), out var instance))
            {
                // First time resolving this type
                // Call factory to create instance
                // Factory can call container.Resolve() for dependencies
                instance = factory(container);

                // Cache instance for future resolves (singleton)
                _singletons[typeof(T)] = instance;
            }

            // Return instance (cached or newly created)
            return instance;
        };
    }

    /// <summary>
    /// Resolves a service instance from the container.
    ///
    /// This is the main entry point for getting service instances.
    ///
    /// Resolution Process:
    /// 1. Check if instance already exists in _singletons
    ///    - If yes: Return cached instance (fast path)
    /// 2. Check if factory exists in _factories
    ///    - If yes: Call factory, cache result, return instance
    /// 3. Neither found: Throw exception (service not registered)
    ///
    /// Singleton Behavior:
    /// - First call: Creates instance via factory or finds pre-registered instance
    /// - Subsequent calls: Returns same instance (singleton)
    ///
    /// Dependency Resolution:
    /// Factories can call container.Resolve() to get their dependencies.
    ///
    /// This allows automatic dependency graph resolution.
    ///
    /// To prevent circular dependency: Design dependencies as a DAG (directed acyclic graph)
    ///
    /// Thread Safety:
    /// - NOT thread-safe
    /// - Only call from single thread (or add locking)
    /// - MyRedis only calls during startup (single-threaded)
    /// </summary>
    /// <typeparam name="T">Service type to resolve</typeparam>
    /// <returns>Service instance (singleton)</returns>
    /// <exception cref="InvalidOperationException">Service not registered</exception>
    public T Resolve<T>() where T : class
    {
        // Check if instance already exists (singleton cache)
        if (_singletons.TryGetValue(typeof(T), out var singleton))
        {
            // Instance already created, return 
            return (T)singleton;
        }

        // Check if factory exists (lazy singleton)
        if (_factories.TryGetValue(typeof(T), out var factory))
        {
            // Call factory to create instance
            // Factory may recursively call Resolve() for dependencies
            // Result is cached by the factory (see RegisterSingleton)
            return (T)factory(this);
        }

        // Service not registered
        // This is a configuration error (forgot to register)
        throw new InvalidOperationException($"Service of type {typeof(T).Name} is not registered.");
    }
}
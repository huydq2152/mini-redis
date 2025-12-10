namespace MyRedis.Infrastructure;

/// <summary>
/// Simple dependency injection (DI) container for managing service lifetimes and dependencies.
///
/// Responsibility: Service Registration and Resolution
/// - Register services (by instance or factory)
/// - Resolve services (create or return cached instances)
/// - Manage singleton lifetimes
/// - Support dependency injection
///
/// What is Dependency Injection?
/// Instead of creating dependencies directly:
///   var dataStore = new InMemoryDataStore();
///   var processor = new CommandProcessor(dataStore, ...);
///
/// Services declare what they need (constructor parameters):
///   public CommandProcessor(IDataStore dataStore, ...)
///
/// And the container provides it:
///   var processor = container.Resolve<CommandProcessor>();
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
/// This is intentionally simple. For production, consider:
/// - Microsoft.Extensions.DependencyInjection
/// - Autofac
/// - Ninject
///
/// Usage Pattern:
/// 1. Register services:
///    container.RegisterSingleton<IDataStore>(new InMemoryDataStore());
///    container.RegisterSingleton<IProcessor>(c => new Processor(c.Resolve<IDataStore>()));
///
/// 2. Resolve root:
///    var server = container.Resolve<RedisServerOrchestrator>();
///
/// 3. Container resolves entire dependency graph automatically
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
    /// Resolve&lt;T&gt;() is called.
    ///
    /// Example:
    /// var dataStore = new InMemoryDataStore();
    /// container.RegisterSingleton&lt;IDataStore&gt;(dataStore);
    ///
    /// Why Use This?
    /// - Simple: No factory needed
    /// - Pre-configured: Instance is already set up
    /// - Immediate: No lazy creation
    ///
    /// When to Use:
    /// - Service has no dependencies
    /// - Instance is already created
    /// - No need for lazy initialization
    ///
    /// Type Parameter:
    /// T is typically an interface (IDataStore) but can be concrete class.
    /// The instance must be assignable to T.
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
    /// Registers a factory for creating instances (non-singleton).
    ///
    /// NOT CURRENTLY USED in MyRedis (all services are singletons).
    ///
    /// Unlike RegisterSingleton(factory), this creates a NEW instance
    /// every time Resolve&lt;T&gt;() is called.
    ///
    /// Use Case:
    /// If we wanted transient lifetime (new instance per resolve):
    /// container.RegisterFactory&lt;Connection&gt;(c => new Connection(socket));
    ///
    /// Each Resolve&lt;Connection&gt;() would create a new Connection.
    ///
    /// Why Not Used?
    /// MyRedis uses singletons for all services (shared state).
    /// </summary>
    /// <typeparam name="T">Service type to register</typeparam>
    /// <param name="factory">Factory function that creates instances</param>
    public void RegisterFactory<T>(Func<ServiceContainer, T> factory) where T : class
    {
        // Store factory in factories dictionary
        // Each resolve calls factory to create new instance (no caching)
        _factories[typeof(T)] = container => factory(container);
    }

    /// <summary>
    /// Registers a singleton factory (lazy singleton creation).
    ///
    /// This is the most common registration method in MyRedis.
    ///
    /// How It Works:
    /// 1. Factory is stored but NOT called yet (lazy)
    /// 2. On first Resolve&lt;T&gt;():
    ///    a. Call factory to create instance
    ///    b. Cache instance in _singletons
    ///    c. Return instance
    /// 3. On subsequent Resolve&lt;T&gt;():
    ///    a. Return cached instance (don't call factory again)
    ///
    /// Why Lazy?
    /// - Dependencies may not be registered yet
    /// - Factory can resolve dependencies from container
    /// - Instance created only when needed
    ///
    /// Example:
    /// container.RegisterSingleton&lt;CommandProcessor&gt;(c => new CommandProcessor(
    ///     c.Resolve&lt;ICommandRegistry&gt;(),
    ///     c.Resolve&lt;IDataStore&gt;(),
    ///     ...
    /// ));
    ///
    /// When Resolve&lt;CommandProcessor&gt;() is called:
    /// 1. Factory executes
    /// 2. Factory calls c.Resolve&lt;ICommandRegistry&gt;() (recursive resolution)
    /// 3. Factory calls c.Resolve&lt;IDataStore&gt;() (recursive resolution)
    /// 4. Factory creates CommandProcessor with resolved dependencies
    /// 5. Instance cached and returned
    ///
    /// Dependency Graph Resolution:
    /// Container automatically resolves the entire dependency graph:
    /// Resolve&lt;RedisServerOrchestrator&gt;()
    ///   -&gt; Resolve&lt;NetworkServer&gt;()
    ///       -&gt; Resolve&lt;IConnectionManager&gt;()
    ///   -&gt; Resolve&lt;CommandProcessor&gt;()
    ///       -&gt; Resolve&lt;ICommandRegistry&gt;()
    ///       -&gt; Resolve&lt;IDataStore&gt;()
    ///       -&gt; Resolve&lt;IExpirationService&gt;()
    ///       -&gt; Resolve&lt;IResponseWriter&gt;()
    ///   -&gt; Resolve&lt;BackgroundTaskManager&gt;()
    ///       -&gt; ... (and so on)
    ///
    /// All resolved automatically with one call!
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
    /// Factories can call container.Resolve() to get their dependencies:
    /// container.RegisterSingleton&lt;Processor&gt;(c => new Processor(
    ///     c.Resolve&lt;IDataStore&gt;()  // Recursive resolve
    /// ));
    ///
    /// This allows automatic dependency graph resolution.
    ///
    /// Example:
    /// var orchestrator = container.Resolve&lt;RedisServerOrchestrator&gt;();
    /// // Container automatically creates all dependencies
    ///
    /// Circular Dependencies:
    /// If A depends on B and B depends on A:
    /// A factory calls c.Resolve&lt;B&gt;()
    /// B factory calls c.Resolve&lt;A&gt;()
    /// Result: StackOverflowException
    ///
    /// To prevent: Design dependencies as a DAG (directed acyclic graph)
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
        // FAST PATH: Check if instance already exists (singleton cache)
        if (_singletons.TryGetValue(typeof(T), out var singleton))
        {
            // Instance already created, return it (cast to T)
            return (T)singleton;
        }

        // SLOW PATH: Check if factory exists (lazy singleton)
        if (_factories.TryGetValue(typeof(T), out var factory))
        {
            // Call factory to create instance
            // Factory may recursively call Resolve() for dependencies
            // Result is cached by the factory (see RegisterSingleton)
            return (T)factory(this);
        }

        // ERROR: Service not registered
        // This is a configuration error (forgot to register)
        throw new InvalidOperationException($"Service of type {typeof(T).Name} is not registered.");
    }

    /// <summary>
    /// Tries to resolve a service instance without throwing on failure.
    ///
    /// This is a safe version of Resolve() that returns null instead of throwing.
    ///
    /// Use When:
    /// - Optional dependencies (service may not be registered)
    /// - Feature detection (check if service available)
    /// - Graceful degradation (fallback if service missing)
    ///
    /// NOT CURRENTLY USED in MyRedis (all dependencies are required).
    ///
    /// Example:
    /// var cache = container.TryResolve&lt;ICache&gt;();
    /// if (cache != null)
    /// {
    ///     // Use cache
    /// }
    /// else
    /// {
    ///     // No cache available, skip caching
    /// }
    ///
    /// Performance:
    /// Uses try-catch which has overhead (avoid in hot paths).
    /// Better approach would be:
    /// - Add TryGetValue-style method
    /// - Return bool + out parameter
    /// But this is simpler for rare usage.
    /// </summary>
    /// <typeparam name="T">Service type to resolve</typeparam>
    /// <returns>Service instance if registered, null otherwise</returns>
    public T? TryResolve<T>() where T : class
    {
        try
        {
            // Try normal resolution
            return Resolve<T>();
        }
        catch
        {
            // Resolution failed (service not registered)
            // Return null instead of throwing
            return null;
        }
    }
}
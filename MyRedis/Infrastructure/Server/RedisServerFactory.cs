using MyRedis.Abstractions.Commands;
using MyRedis.Abstractions.Network;
using MyRedis.Abstractions.Storage;
using MyRedis.Commands;
using MyRedis.Core.Background;
using MyRedis.Core.Storage;
using MyRedis.Infrastructure.Background;
using MyRedis.Infrastructure.Commands;
using MyRedis.Infrastructure.DependencyInjection;
using MyRedis.Infrastructure.Network;
using MyRedis.Services.Commands;
using MyRedis.Services.Network;
using MyRedis.Services.Storage;
using MyRedis.System.Tasks;
using MyRedis.System.Workers;

namespace MyRedis.Infrastructure.Server;

/// <summary>
/// Factory for configuring and creating the Redis server with all dependencies.
///
/// Responsibility: Server Configuration and Dependency Wiring
/// - Create and register all service instances
/// - Wire up dependencies between services
/// - Register all command handlers
/// - Build the complete object graph
/// - Return a fully-configured RedisServerOrchestrator
///
/// Design Pattern: Factory Pattern + Builder Pattern
/// - Factory: Creates complex object graphs
/// - Builder: Builds the server step-by-step
/// - Abstract Factory: Could support different server configurations
///
/// Service Registration Order:
/// 1. Core Services: Data store, expiration, connections
/// 2. Command Handlers: GET, SET, DEL, etc.
/// 3. Infrastructure: Network, processor, orchestrator
///
/// Order matters because later services depend on earlier ones.
///
/// Dependency Injection Container:
/// Uses ServiceContainer (simple DI container) to:
/// - Register services as singletons
/// - Resolve dependencies automatically
/// - Avoid manual new() everywhere
///
/// Why Singletons?
/// - Server components are stateful (data store, connections)
/// - Only one instance of each should exist
/// - All components share the same data store, etc.
/// </summary>
public static class RedisServerFactory
{
    /// <summary>
    /// Creates and configures a fully-functional Redis server.
    ///
    /// This is the main entry point for server creation. It handles all
    /// configuration and returns a ready-to-run server.
    ///
    /// The returned orchestrator has all dependencies injected and is
    /// ready to start the event loop via RunAsync().
    ///
    /// Configuration:
    /// - Port: TCP port to listen on (default: 6379, same as Redis)
    /// - All other settings are hardcoded (simple server, no config file)
    ///
    /// Why Static Method?
    /// - No state needed between calls
    /// - Each call creates a new server
    /// - Simpler than instantiating a factory class
    ///
    /// Thread Safety:
    /// - Safe to call from multiple threads
    /// - Each call creates independent server instance
    /// - Servers don't share state
    /// </summary>
    /// <param name="port">TCP port to listen on (default: 6379)</param>
    /// <returns>Tuple of (orchestrator, backgroundTaskSystem) - orchestrator ready to run, system for shutdown</returns>
    public static (RedisServerOrchestrator orchestrator, BackgroundTaskSystem backgroundTaskSystem)
        CreateServer(int port = 6379)
    {
        // Create the dependency injection container
        // This manages all service instances and resolves dependencies
        var container = new ServiceContainer();

        // STEP 1: Register core services
        // These are the fundamental services (data store, expiration, etc.)
        // Must be registered first because other components depend on them
        RegisterCoreServices(container);

        // STEP 2: Register command handlers
        // Register all Redis command implementations (GET, SET, DEL, etc.)
        // Handlers are independent of each other (can register in any order)
        RegisterCommandHandlers(container);

        // STEP 3: Register infrastructure components
        // High-level components (network, processor, orchestrator)
        // Must be registered last because they depend on core services
        RegisterInfrastructureComponents(container, port);

        // STEP 4: Resolve and return the orchestrator and background task system
        // Container automatically resolves all dependencies recursively
        // Returns fully-configured server ready to run plus background system for shutdown control
        return (
            container.Resolve<RedisServerOrchestrator>(),
            container.Resolve<BackgroundTaskSystem>()
        );
    }

    /// <summary>
    /// Registers core services that provide fundamental server functionality.
    ///
    /// Why Adapters?
    /// - ExpirationManager has complex API, IExpirationService simplifies it
    /// - IdleManager has intrusive linked list, IConnectionManager hides it
    /// - Adapters allow replacing implementation without changing consumers
    ///
    /// Service Lifetime:
    /// All services are registered as singletons:
    /// - Only one instance exists for the entire server
    /// - All components share the same data store, expiration service, etc.
    /// - This is critical for correctness (can't have multiple data stores!)
    ///
    /// Registration Types:
    /// - RegisterSingleton(instance): Direct instance
    /// - RegisterSingleton(factory): Lazy creation via factory
    /// The factory approach allows resolving dependencies from the container.
    /// </summary>
    private static void RegisterCoreServices(ServiceContainer container)
    {
        // Register managers first (other services may depend on them)
        // These are concrete classes that predate the service abstraction layer
        container.RegisterSingleton(new ExpirationManager());
        container.RegisterSingleton(new IdleManager());
        container.RegisterSingleton(new BackgroundTaskSystem());

        // Register service abstractions (interfaces)
        // These provide clean APIs for core functionality
        container.RegisterSingleton<IDataStore>(new InMemoryDataStore());
        container.RegisterSingleton<IResponseWriter>(new ResponseWriterService());
        container.RegisterSingleton<ICommandRegistry>(new CommandRegistry());
        container.RegisterSingleton<IExpirationService>(c =>
            new ExpirationService(c.Resolve<ExpirationManager>()));
        container.RegisterSingleton<IConnectionManager>(c =>
            new ConnectionManager(c.Resolve<IdleManager>()));
    }

    /// <summary>
    /// Registers all Redis command handlers.
    ///
    /// Command Handlers:
    /// Each handler implements a single command
    ///
    /// Handler Registration:
    /// - Create handler instance
    /// - Add to handlers array
    /// - Loop registers each handler with CommandRegistry
    /// - Registry maps command name (from handler.CommandName) to handler
    ///
    /// Why Array + Loop?
    /// - Easy to see all commands in one place
    /// - Adding new command = one line
    /// - Could be loaded from plugins in the future
    ///
    /// Handler Dependencies:
    /// - Most handlers have no dependencies (stateless)
    /// - DelCommandHandler needs BackgroundTaskSystem (for categorized deferred cleanup)
    /// - Dependencies are passed via constructor
    /// </summary>
    private static void RegisterCommandHandlers(ServiceContainer container)
    {
        // Resolve dependencies needed by some handlers
        var registry = container.Resolve<ICommandRegistry>();
        var backgroundTaskSystem = container.Resolve<BackgroundTaskSystem>();

        // Create instances of all command handlers
        // Most handlers are stateless (no constructor parameters)
        // DelCommandHandler needs BackgroundTaskSystem for deferred cleanup
        var handlers = new ICommandHandler[]
        {
            new GetCommandHandler(),
            new SetCommandHandler(),
            new DelCommandHandler(backgroundTaskSystem),
            new KeysCommandHandler(),
            new ScanCommandHandler(),
            new PingCommandHandler(),
            new EchoCommandHandler(),
            new ExpireCommandHandler(),
            new TtlCommandHandler(),
            new ZAddCommandHandler(),
            new ZRangeCommandHandler(),
            new ZRemCommandHandler()
        };

        // Register each handler with the command registry
        // Registry.Register() extracts command name from handler.CommandName and creates mapping
        foreach (var handler in handlers)
        {
            registry.Register(handler);
        }
    }

    /// <summary>
    /// Registers infrastructure components that coordinate the server.
    /// 
    /// Factory Functions:
    /// Each component uses a factory (c => new Component(...))
    /// The factory:
    /// 1. Is called when the component is first needed
    /// 2. Can resolve dependencies from container (c.Resolve<T>())
    /// 3. Creates the component with all dependencies injected
    /// 4. Result is cached (singleton lifetime)
    ///
    /// Why Factories Instead of Direct Registration?
    /// - Components have dependencies that aren't available yet
    /// - Factories allow lazy resolution (when dependencies are ready)
    /// - Container automatically resolves the dependency graph
    /// </summary>
    private static void RegisterInfrastructureComponents(ServiceContainer container, int port)
    {
        container.RegisterSingleton<NetworkServer>(c =>
            new NetworkServer(
                c.Resolve<IConnectionManager>(),
                port));
        
        container.RegisterSingleton<CommandProcessor>(c =>
            new CommandProcessor(
                c.Resolve<ICommandRegistry>(),
                c.Resolve<IDataStore>(),
                c.Resolve<IExpirationService>(),
                c.Resolve<IResponseWriter>()));

        container.RegisterSingleton<BackgroundTaskManager>(c =>
        {
            var manager = new BackgroundTaskManager();
            
            manager.Register(new ExpirationTask(
                c.Resolve<IDataStore>(),
                c.Resolve<IExpirationService>()));

            manager.Register(new IdleConnectionCleanupTask(
                c.Resolve<IConnectionManager>(),
                c.Resolve<NetworkServer>()));

            // Future tasks can be added here without modifying BackgroundTaskManager: metric, snapshot, heartbeat, etc.

            return manager;
        });
        
        container.RegisterSingleton<RedisServerOrchestrator>(c =>
            new RedisServerOrchestrator(
                c.Resolve<NetworkServer>(),
                c.Resolve<CommandProcessor>(),
                c.Resolve<BackgroundTaskManager>()));
    }
}
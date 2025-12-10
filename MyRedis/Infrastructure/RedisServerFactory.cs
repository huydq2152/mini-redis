using MyRedis.Abstractions;
using MyRedis.Commands;
using MyRedis.Core;
using MyRedis.Services;
using MyRedis.System;

namespace MyRedis.Infrastructure;

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
/// Why a Factory?
/// Creating a Redis server requires:
/// 1. ~15 different service instances
/// 2. Complex dependency relationships
/// 3. Specific initialization order
/// 4. Registering all command handlers
///
/// Without a factory, Program.cs would have dozens of lines of configuration.
/// The factory encapsulates all this complexity in one place.
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
///
/// Extensibility:
/// To add a new command:
/// 1. Create handler class (inherit BaseCommandHandler)
/// 2. Add to handlers array in RegisterCommandHandlers()
/// Done!
///
/// To add a new service:
/// 1. Define interface in Abstractions/
/// 2. Create implementation
/// 3. Register in RegisterCoreServices()
/// 4. Inject where needed
///
/// Example Usage:
/// var server = RedisServerFactory.CreateServer(port: 6379);
/// await server.RunAsync();
/// </summary>
public static class RedisServerFactory
{
    /// <summary>
    /// Creates and configures a fully-functional Redis server.
    ///
    /// This is the main entry point for server creation. It handles all
    /// configuration and returns a ready-to-run server.
    ///
    /// Process:
    /// 1. Create dependency injection container
    /// 2. Register core services (data store, expiration, etc.)
    /// 3. Register command handlers (GET, SET, DEL, etc.)
    /// 4. Register infrastructure components (network, processor, orchestrator)
    /// 5. Resolve and return orchestrator
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
    /// <returns>Fully-configured RedisServerOrchestrator ready to run</returns>
    public static RedisServerOrchestrator CreateServer(int port = 6379)
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

        // STEP 4: Resolve and return the orchestrator
        // Container automatically resolves all dependencies recursively
        // Returns fully-configured server ready to run
        return container.Resolve<RedisServerOrchestrator>();
    }

    /// <summary>
    /// Registers core services that provide fundamental server functionality.
    ///
    /// Core Services:
    /// - ExpirationManager: Legacy component for TTL tracking (min-heap)
    /// - IdleManager: Legacy component for idle connection tracking (intrusive list)
    /// - BackgroundWorker: Legacy component for deferred operations
    /// - IDataStore: In-memory key-value storage (thread-safe)
    /// - IResponseWriter: Redis protocol response formatting
    /// - ICommandRegistry: Command handler registration and lookup
    /// - IExpirationService: Adapter for ExpirationManager
    /// - IConnectionManager: Adapter for IdleManager
    ///
    /// Legacy Components:
    /// ExpirationManager, IdleManager, and BackgroundWorker are "legacy"
    /// in the sense that they were created before the service abstraction layer.
    /// They're wrapped by IExpirationService and IConnectionManager adapters
    /// to provide a cleaner interface.
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
        // Register legacy managers first (other services may depend on them)
        // These are concrete classes that predate the service abstraction layer

        // ExpirationManager: Min-heap for tracking key expiration times
        container.RegisterSingleton(new ExpirationManager());

        // IdleManager: Intrusive linked list for tracking connection activity
        container.RegisterSingleton(new IdleManager());

        // BackgroundWorker: Legacy component for deferred operations
        container.RegisterSingleton(new BackgroundWorker());

        // Register service abstractions (interfaces)
        // These provide clean APIs for core functionality

        // IDataStore: In-memory key-value storage with thread-safe operations
        container.RegisterSingleton<IDataStore>(new InMemoryDataStore());

        // IResponseWriter: Formats responses in Redis protocol
        container.RegisterSingleton<IResponseWriter>(new ResponseWriterService());

        // ICommandRegistry: Maps command names to handler instances
        container.RegisterSingleton<ICommandRegistry>(new CommandRegistry());

        // IExpirationService: Adapter for ExpirationManager
        // Factory resolves ExpirationManager from container and wraps it
        container.RegisterSingleton<IExpirationService>(c =>
            new ExpirationService(c.Resolve<ExpirationManager>()));

        // IConnectionManager: Adapter for IdleManager
        // Factory resolves IdleManager from container and wraps it
        container.RegisterSingleton<IConnectionManager>(c =>
            new ConnectionManager(c.Resolve<IdleManager>()));
    }

    /// <summary>
    /// Registers all Redis command handlers.
    ///
    /// Command Handlers:
    /// Each handler implements a single Redis command:
    /// - GetCommandHandler: GET key
    /// - SetCommandHandler: SET key value
    /// - DelCommandHandler: DEL key [key ...]
    /// - KeysCommandHandler: KEYS pattern
    /// - PingCommandHandler: PING [message]
    /// - EchoCommandHandler: ECHO message
    /// - ExpireCommandHandler: EXPIRE key seconds
    /// - TtlCommandHandler: TTL key
    /// - ZAddCommandHandler: ZADD key score member
    /// - ZRangeCommandHandler: ZRANGE key start stop
    ///
    /// Handler Registration:
    /// - Create handler instance
    /// - Add to handlers array
    /// - Loop registers each handler with CommandRegistry
    /// - Registry maps command name (from handler.CommandName) to handler
    ///
    /// Adding New Commands:
    /// To add a new command:
    /// 1. Create class inheriting BaseCommandHandler
    /// 2. Implement CommandName property and HandleAsync() method
    /// 3. Add to handlers array below
    /// 4. Done! No other changes needed.
    ///
    /// Why Array + Loop?
    /// - Easy to see all commands in one place
    /// - Adding new command = one line
    /// - Could be loaded from plugins in the future
    ///
    /// Handler Dependencies:
    /// - Most handlers have no dependencies (stateless)
    /// - DelCommandHandler needs BackgroundWorker (for deferred cleanup)
    /// - Dependencies are passed via constructor
    /// </summary>
    private static void RegisterCommandHandlers(ServiceContainer container)
    {
        // Resolve dependencies needed by some handlers
        var registry = container.Resolve<ICommandRegistry>();
        var backgroundWorker = container.Resolve<BackgroundWorker>();

        // Create instances of all command handlers
        // Most handlers are stateless (no constructor parameters)
        // DelCommandHandler needs BackgroundWorker for deferred cleanup
        var handlers = new ICommandHandler[]
        {
            new GetCommandHandler(),                     // GET key
            new SetCommandHandler(),                     // SET key value
            new DelCommandHandler(backgroundWorker),     // DEL key [key ...] (needs BackgroundWorker)
            new KeysCommandHandler(),                    // KEYS pattern
            new PingCommandHandler(),                    // PING [message]
            new EchoCommandHandler(),                    // ECHO message
            new ExpireCommandHandler(),                  // EXPIRE key seconds
            new TtlCommandHandler(),                     // TTL key
            new ZAddCommandHandler(),                    // ZADD key score member
            new ZRangeCommandHandler()                   // ZRANGE key start stop
        };

        // Register each handler with the command registry
        // Registry.Register() extracts command name from handler.CommandName
        // and creates mapping: "GET" -> GetCommandHandler instance
        foreach (var handler in handlers)
        {
            registry.Register(handler);
        }
    }

    /// <summary>
    /// Registers infrastructure components that coordinate the server.
    ///
    /// Infrastructure Components:
    /// - NetworkServer: TCP networking, connection lifecycle
    /// - CommandProcessor: Protocol parsing, command routing
    /// - BackgroundTaskManager: Maintenance tasks (expiration, idle cleanup)
    /// - RedisServerOrchestrator: Event loop coordination
    ///
    /// Dependency Chain:
    /// RedisServerOrchestrator depends on:
    /// - NetworkServer
    /// - CommandProcessor
    /// - BackgroundTaskManager
    ///
    /// BackgroundTaskManager depends on:
    /// - IDataStore
    /// - IExpirationService
    /// - IConnectionManager
    /// - NetworkServer
    ///
    /// CommandProcessor depends on:
    /// - ICommandRegistry
    /// - IDataStore
    /// - IExpirationService
    /// - IResponseWriter
    ///
    /// NetworkServer depends on:
    /// - IConnectionManager
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
    ///
    /// Registration Order:
    /// Order doesn't matter here (factories defer creation)
    /// But conceptually: low-level to high-level
    /// - NetworkServer (lowest level, few dependencies)
    /// - CommandProcessor (mid-level)
    /// - BackgroundTaskManager (mid-level)
    /// - RedisServerOrchestrator (highest level, depends on others)
    /// </summary>
    private static void RegisterInfrastructureComponents(ServiceContainer container, int port)
    {
        // Register NetworkServer
        // Handles TCP networking and connection lifecycle
        // Depends on: IConnectionManager (for activity tracking)
        // Parameter: port (passed from CreateServer)
        container.RegisterSingleton<NetworkServer>(c =>
            new NetworkServer(
                c.Resolve<IConnectionManager>(),  // Track connection activity
                port));                           // TCP port to listen on

        // Register CommandProcessor
        // Parses Redis protocol and routes commands to handlers
        // Depends on: ICommandRegistry, IDataStore, IExpirationService, IResponseWriter
        container.RegisterSingleton<CommandProcessor>(c =>
            new CommandProcessor(
                c.Resolve<ICommandRegistry>(),    // To look up command handlers
                c.Resolve<IDataStore>(),          // To pass to handlers (for data access)
                c.Resolve<IExpirationService>(),  // To pass to handlers (for TTL management)
                c.Resolve<IResponseWriter>()));   // To pass to handlers (for response formatting)

        // Register BackgroundTaskManager
        // Coordinates background maintenance tasks
        // Depends on: IDataStore, IExpirationService, IConnectionManager, NetworkServer
        container.RegisterSingleton<BackgroundTaskManager>(c =>
            new BackgroundTaskManager(
                c.Resolve<IDataStore>(),          // To delete expired keys
                c.Resolve<IExpirationService>(),  // To find expired keys
                c.Resolve<IConnectionManager>(),  // To find idle connections
                c.Resolve<NetworkServer>()));     // To close idle connections

        // Register RedisServerOrchestrator
        // Coordinates the main event loop
        // Depends on: NetworkServer, CommandProcessor, BackgroundTaskManager
        // This is the top-level component (the "root" of the dependency graph)
        container.RegisterSingleton<RedisServerOrchestrator>(c =>
            new RedisServerOrchestrator(
                c.Resolve<NetworkServer>(),           // For network I/O
                c.Resolve<CommandProcessor>(),        // For command processing
                c.Resolve<BackgroundTaskManager>())); // For background tasks
    }
}
using MyRedis.Abstractions;
using MyRedis.Commands;
using MyRedis.Core;
using MyRedis.Services;
using MyRedis.System;

namespace MyRedis.Infrastructure;

/// <summary>
/// Factory for configuring and creating the Redis server with all dependencies
/// </summary>
public static class RedisServerFactory
{
    /// <summary>
    /// Creates and configures a Redis server with all dependencies
    /// </summary>
    /// <param name="port">Port to listen on</param>
    /// <returns>Configured Redis server orchestrator</returns>
    public static RedisServerOrchestrator CreateServer(int port = 6379)
    {
        var container = new ServiceContainer();
        
        // Register core services
        RegisterCoreServices(container);
        
        // Register command handlers
        RegisterCommandHandlers(container);
        
        // Register infrastructure components
        RegisterInfrastructureComponents(container, port);
        
        return container.Resolve<RedisServerOrchestrator>();
    }

    private static void RegisterCoreServices(ServiceContainer container)
    {
        // Register existing managers (adapters for legacy code)
        container.RegisterSingleton(new ExpirationManager());
        container.RegisterSingleton(new IdleManager());
        container.RegisterSingleton(new BackgroundWorker());

        // Register abstraction services
        container.RegisterSingleton<IDataStore>(new InMemoryDataStore());
        container.RegisterSingleton<IResponseWriter>(new ResponseWriterService());
        container.RegisterSingleton<ICommandRegistry>(new CommandRegistry());
        
        container.RegisterSingleton<IExpirationService>(c => 
            new ExpirationService(c.Resolve<ExpirationManager>()));
        
        container.RegisterSingleton<IConnectionManager>(c => 
            new ConnectionManager(c.Resolve<IdleManager>()));
    }

    private static void RegisterCommandHandlers(ServiceContainer container)
    {
        var registry = container.Resolve<ICommandRegistry>();
        var backgroundWorker = container.Resolve<BackgroundWorker>();

        // Register all command handlers
        var handlers = new ICommandHandler[]
        {
            new GetCommandHandler(),
            new SetCommandHandler(),
            new DelCommandHandler(backgroundWorker),
            new KeysCommandHandler(),
            new PingCommandHandler(),
            new EchoCommandHandler(),
            new ExpireCommandHandler(),
            new TtlCommandHandler(),
            new ZAddCommandHandler(),
            new ZRangeCommandHandler()
        };

        foreach (var handler in handlers)
        {
            registry.Register(handler);
        }
    }

    private static void RegisterInfrastructureComponents(ServiceContainer container, int port)
    {
        // Register infrastructure components
        container.RegisterSingleton<NetworkServer>(c => 
            new NetworkServer(c.Resolve<IConnectionManager>(), port));

        container.RegisterSingleton<CommandProcessor>(c => 
            new CommandProcessor(
                c.Resolve<ICommandRegistry>(),
                c.Resolve<IDataStore>(),
                c.Resolve<IExpirationService>(),
                c.Resolve<IResponseWriter>()));

        container.RegisterSingleton<BackgroundTaskManager>(c => 
            new BackgroundTaskManager(
                c.Resolve<IDataStore>(),
                c.Resolve<IExpirationService>(),
                c.Resolve<IConnectionManager>(),
                c.Resolve<NetworkServer>()));

        container.RegisterSingleton<RedisServerOrchestrator>(c => 
            new RedisServerOrchestrator(
                c.Resolve<NetworkServer>(),
                c.Resolve<CommandProcessor>(),
                c.Resolve<BackgroundTaskManager>()));
    }
}
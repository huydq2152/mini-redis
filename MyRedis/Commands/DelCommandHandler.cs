using MyRedis.Abstractions;
using MyRedis.Abstractions.Commands;
using MyRedis.Abstractions.Configuration;
using MyRedis.System;
using MyRedis.System.Tasks;

namespace MyRedis.Commands;

/// <summary>
/// Handler for the DEL command which removes keys from the Redis database.
/// Implements both synchronous and asynchronous deletion strategies based on object size.
/// Large objects are deleted in the background to prevent blocking the main thread.
/// </summary>
public class DelCommandHandler : BaseCommandHandler
{
    /// <summary>
    /// Background task system for handling asynchronous deletion of large objects.
    /// Uses the LazyFree category to prevent the main Redis thread from being blocked
    /// during expensive cleanup operations.
    /// </summary>
    private readonly BackgroundTaskSystem _backgroundTaskSystem;

    /// <summary>
    /// Configuration service for accessing runtime parameters.
    /// Used to read the lazyfree-lazy-server-del threshold dynamically (hot-reload).
    /// </summary>
    private readonly IConfigurationService _configService;

    /// <summary>
    /// Initializes a new instance of the DelCommandHandler with the required dependencies.
    /// </summary>
    /// <param name="backgroundTaskSystem">The background task system for categorized asynchronous operations</param>
    /// <param name="configService">The configuration service for runtime parameter access</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null</exception>
    public DelCommandHandler(BackgroundTaskSystem backgroundTaskSystem, IConfigurationService configService)
    {
        _backgroundTaskSystem = backgroundTaskSystem ?? throw new ArgumentNullException(nameof(backgroundTaskSystem));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
    }

    /// <summary>
    /// Gets the Redis command name that this handler processes.
    /// </summary>
    public override string CommandName => "DEL";

    /// <summary>
    /// Handles the DEL command execution, removing the specified key from the database.
    /// Supports both synchronous and asynchronous deletion based on object complexity.
    /// </summary>
    /// <param name="context">The command execution context</param>
    /// <param name="args">Command arguments - expects exactly one argument (the key to delete)</param>
    /// <returns>A task that completes when the command has been processed</returns>
    public override Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args)
    {
        // Validate command syntax: DEL requires exactly one key argument
        if (args.Count != 1)
        {
            WriteWrongArgsError(context);
            return Task.FromResult(true);
        }

        string key = args[0];
        var value = context.DataStore.Get(key);

        if (value != null)
        {
            // Remove the key from the data store and clear any expiration tracking
            context.DataStore.Remove(key);
            context.ExpirationService.RemoveExpiration(key);

            // Read threshold from config (hot-reload support!)
            // This allows runtime adjustment via CONFIG SET lazyfree-lazy-server-del <value>
            var threshold = _configService.Get<int>("lazyfree-lazy-server-del");

            // Choose deletion strategy based on object size threshold
            if (IsLargeObject(value, threshold))
            {
                // Large objects: Use asynchronous deletion to avoid blocking
                // This implements Redis UNLINK-like behavior for better performance
                // Submits to LazyFree category for isolation from other background operations
                _backgroundTaskSystem.Submit(BackgroundTaskCategory.LazyFree, () => DestroyObject(value));
            }
            else
            {
                // Small objects: Delete immediately on the main thread
                // Async overhead would be greater than deletion cost
                DestroyObject(value);
            }

            // Return 1 to indicate one key was successfully deleted
            context.ResponseWriter.WriteInt(context.Connection.Writer, 1);
        }
        else
        {
            // Key doesn't exist, return 0 to indicate no keys were deleted
            context.ResponseWriter.WriteInt(context.Connection.Writer, 0);
        }

        return Task.FromResult(true);
    }

    /// <summary>
    /// Determines whether an object is considered "large" and should be deleted asynchronously.
    /// Uses a threshold-based approach to balance performance vs overhead.
    ///
    /// Performance Analysis:
    /// - Small objects (< threshold): Sync deletion is faster (overhead > benefit)
    /// - Large objects (>= threshold): Async deletion prevents main thread blocking
    /// </summary>
    /// <param name="val">The object to evaluate</param>
    /// <param name="threshold">The threshold for determining large objects</param>
    /// <returns>True if the object should be deleted asynchronously, false for immediate deletion</returns>
    private static bool IsLargeObject(object val, int threshold)
    {
        if (val is Storage.DataStructures.SortedSet sortedSet)
        {
            // Only consider it "large" if it exceeds the threshold
            // This prevents unnecessary async overhead for small sorted sets
            return sortedSet.Count >= threshold;
        }

        // Simple objects like strings are always considered lightweight
        // Future enhancement: Add thresholds for other data structures (Hash, List, etc.)
        return false;
    }

    /// <summary>
    /// Performs the actual cleanup/destruction of an object.
    ///
    /// Design Principle: "Tell, Don't Ask"
    /// This method tells objects to destroy themselves rather than asking about their internal structure.
    /// Each object knows how to properly clean itself up, maintaining encapsulation.
    ///
    /// For complex data structures like SortedSet, destruction involves breaking internal references
    /// to help the garbage collector and reduce GC pause times. See SortedSet.Destroy() for details.
    /// </summary>
    /// <param name="val">The object to destroy</param>
    private static void DestroyObject(object val)
    {
        // Tell the object to destroy itself - the object knows its own internal structure
        // and how to properly clean up. We don't need to know the details.
        if (val is Storage.DataStructures.SortedSet sortedSet)
        {
            // Delegate to the object's own Destroy method
            // This maintains encapsulation and follows OOP principles
            sortedSet.Destroy();
        }
    }
}
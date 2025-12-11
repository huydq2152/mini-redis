using MyRedis.Abstractions;
using MyRedis.System;

namespace MyRedis.Commands;

/// <summary>
/// Handler for the DEL command which removes keys from the Redis database.
/// Implements both synchronous and asynchronous deletion strategies based on object size.
/// Large objects are deleted in the background to prevent blocking the main thread.
/// </summary>
public class DelCommandHandler : BaseCommandHandler
{
    /// <summary>
    /// Threshold for determining when to use async deletion (lazyfree).
    /// Objects with fewer elements than this threshold are deleted synchronously.
    /// Objects with this many or more elements are deleted asynchronously to avoid blocking.
    ///
    /// This value matches Redis's lazyfree-lazy-server-del configuration default.
    ///
    /// Rationale:
    /// - Sync deletion of small objects (~1-63 elements): ~100 nanoseconds
    /// - Async overhead (Action allocation, Channel enqueue, context switch): ~few microseconds
    /// - Below threshold: Overhead > Benefit, so use sync
    /// - Above threshold: Benefit > Overhead, so use async
    /// </summary>
    private const int LazyFreeThreshold = 64;

    /// <summary>
    /// Background worker for handling asynchronous deletion of large objects.
    /// This prevents the main Redis thread from being blocked during expensive cleanup operations.
    /// </summary>
    private readonly BackgroundWorker _backgroundWorker;

    /// <summary>
    /// Initializes a new instance of the DelCommandHandler with the required background worker.
    /// </summary>
    /// <param name="backgroundWorker">The background worker for asynchronous operations</param>
    /// <exception cref="ArgumentNullException">Thrown when backgroundWorker is null</exception>
    public DelCommandHandler(BackgroundWorker backgroundWorker)
    {
        _backgroundWorker = backgroundWorker ?? throw new ArgumentNullException(nameof(backgroundWorker));
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

            // Choose deletion strategy based on object size threshold
            if (IsLargeObject(value))
            {
                // Large objects (>= 64 elements): Use asynchronous deletion to avoid blocking
                // This implements Redis UNLINK-like behavior for better performance
                _backgroundWorker.Submit(() => DestroyObject(value));
            }
            else
            {
                // Small objects (< 64 elements): Delete immediately on the main thread
                // Async overhead would be greater than deletion cost
                DestroyObject(value);
            }

            // Return 1 to indicate one key was successfully deleted
            context.ResponseWriter.WriteInt(context.Connection.WriteBuffer, 1);
        }
        else
        {
            // Key doesn't exist, return 0 to indicate no keys were deleted
            context.ResponseWriter.WriteInt(context.Connection.WriteBuffer, 0);
        }

        return Task.FromResult(true);
    }

    /// <summary>
    /// Determines whether an object is considered "large" and should be deleted asynchronously.
    /// Uses a threshold-based approach to balance performance vs overhead.
    ///
    /// Performance Analysis:
    /// - Small objects (< 64 elements): Sync deletion is faster (overhead > benefit)
    /// - Large objects (>= 64 elements): Async deletion prevents main thread blocking
    /// </summary>
    /// <param name="val">The object to evaluate</param>
    /// <returns>True if the object should be deleted asynchronously, false for immediate deletion</returns>
    private static bool IsLargeObject(object val)
    {
        if (val is Storage.DataStructures.SortedSet sortedSet)
        {
            // Only consider it "large" if it exceeds the threshold
            // This prevents unnecessary async overhead for small sorted sets
            return sortedSet.Count >= LazyFreeThreshold;
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
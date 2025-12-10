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

            // Choose deletion strategy based on object complexity
            if (IsLargeObject(value))
            {
                // Large/complex objects: Use asynchronous deletion to avoid blocking
                // This implements Redis UNLINK-like behavior for better performance
                Console.WriteLine($"[Async] Unlinking large key: {key}");
                _backgroundWorker.Submit(() => DestroyObject(value));
            }
            else
            {
                // Small objects: Delete immediately on the main thread
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
    /// Large objects are those that might take significant time to clean up and could
    /// block the main Redis processing thread.
    /// </summary>
    /// <param name="val">The object to evaluate</param>
    /// <returns>True if the object should be deleted asynchronously, false for immediate deletion</returns>
    private static bool IsLargeObject(object val)
    {
        if (val is Storage.DataStructures.SortedSet)
        {
            // SortedSets with AVL trees can be complex and time-consuming to destroy
            // For demonstration purposes, we consider all sorted sets as large objects
            return true;
        }

        // Simple objects like strings are considered lightweight
        return false;
    }

    /// <summary>
    /// Performs the actual cleanup/destruction of an object.
    /// For complex data structures, this method helps the garbage collector by
    /// breaking circular references and reducing GC pressure on higher generations.
    /// </summary>
    /// <param name="val">The object to destroy</param>
    private static void DestroyObject(object val)
    {
        // While C#'s garbage collector handles memory cleanup automatically,
        // we can assist with complex structures like AVL trees to prevent
        // stack overflow during recursive GC and reduce Gen 2 pressure

        if (val is Storage.DataStructures.SortedSet)
        {
            // Simulate expensive cleanup operation for demonstration
            // In a real implementation, this would traverse the AVL tree
            // and explicitly null out node references to help the GC
            Thread.Sleep(500); // Demo: Simulate 500ms cleanup time
            Console.WriteLine("[BgWorker] Large object destroyed.");
        }
        
        // For simple objects like strings, no special cleanup is needed
        // The GC will handle them efficiently
    }
}
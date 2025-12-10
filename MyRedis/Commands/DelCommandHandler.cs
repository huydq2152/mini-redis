using MyRedis.Abstractions;
using MyRedis.System;

namespace MyRedis.Commands;

/// <summary>
/// Handler for the DEL command
/// </summary>
public class DelCommandHandler : BaseCommandHandler
{
    private readonly BackgroundWorker _backgroundWorker;

    public DelCommandHandler(BackgroundWorker backgroundWorker)
    {
        _backgroundWorker = backgroundWorker ?? throw new ArgumentNullException(nameof(backgroundWorker));
    }

    public override string CommandName => "DEL";

    public override Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args)
    {
        if (args.Count != 1)
        {
            WriteWrongArgsError(context);
            return Task.FromResult(true);
        }

        string key = args[0];
        var value = context.DataStore.Get(key);

        if (value != null)
        {
            // Remove from store and expiration tracking
            context.DataStore.Remove(key);
            context.ExpirationService.RemoveExpiration(key);

            // Decide how to destroy the object
            if (IsLargeObject(value))
            {
                // Heavy operation -> Send to background (Async Unlink)
                Console.WriteLine($"[Async] Unlinking large key: {key}");
                _backgroundWorker.Submit(() => DestroyObject(value));
            }
            else
            {
                // Light operation -> Destroy immediately (Sync)
                DestroyObject(value);
            }

            context.ResponseWriter.WriteInt(context.Connection.WriteBuffer, 1);
        }
        else
        {
            context.ResponseWriter.WriteInt(context.Connection.WriteBuffer, 0);
        }

        return Task.FromResult(true);
    }

    private static bool IsLargeObject(object val)
    {
        if (val is Storage.DataStructures.SortedSet)
        {
            // For demonstration, consider all sorted sets as large
            return true;
        }

        return false; // String considered light
    }

    private static void DestroyObject(object val)
    {
        // In C#, GC handles cleanup automatically.
        // For complex structures like AVL Tree, we can help GC
        // by breaking references to avoid Stack Overflow during recursive GC
        // or reduce pressure on Gen 2.

        if (val is Storage.DataStructures.SortedSet)
        {
            // Simulate heavy work:
            // Traverse tree and set nodes to null (or just sleep for demo)
            Thread.Sleep(500); // Demo: Pretend this takes 500ms
            Console.WriteLine("[BgWorker] Large object destroyed.");
        }
    }
}
using MyRedis.Abstractions.Network;
using MyRedis.Abstractions.Storage;
using MyRedis.Core.Network;

namespace MyRedis.Abstractions.Commands;

/// <summary>
/// Provides execution context for command handlers.
/// </summary>
public interface ICommandContext
{
    /// <summary>
    /// Gets the connection associated with this command execution.
    /// </summary>
    Connection Connection { get; }

    /// <summary>
    /// Gets the data store service for key-value operations.
    /// </summary>
    IDataStore DataStore { get; }

    /// <summary>
    /// Gets the expiration service for TTL management.
    /// </summary>
    IExpirationService ExpirationService { get; }

    /// <summary>
    /// Gets the response writer for sending protocol-formatted responses.
    /// Use this instead of writing bytes directly to maintain protocol consistency.
    /// </summary>
    IResponseWriter ResponseWriter { get; }
}
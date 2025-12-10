using MyRedis.Core;

namespace MyRedis.Abstractions;

/// <summary>
/// Provides execution context for command handlers
/// </summary>
public interface ICommandContext
{
    /// <summary>
    /// Gets the connection associated with this command execution
    /// </summary>
    Connection Connection { get; }

    /// <summary>
    /// Gets the data store service
    /// </summary>
    IDataStore DataStore { get; }

    /// <summary>
    /// Gets the expiration manager service
    /// </summary>
    IExpirationService ExpirationService { get; }

    /// <summary>
    /// Gets the response writer for sending responses back to the client
    /// </summary>
    IResponseWriter ResponseWriter { get; }
}
using MyRedis.Abstractions;
using MyRedis.Core;

namespace MyRedis.Services;

/// <summary>
/// Service adapter for the existing IdleManager
/// </summary>
public class ConnectionManager : IConnectionManager
{
    private readonly IdleManager _idleManager;

    public ConnectionManager(IdleManager idleManager)
    {
        _idleManager = idleManager ?? throw new ArgumentNullException(nameof(idleManager));
    }

    public void Add(Connection connection)
    {
        _idleManager.Add(connection);
    }

    public void Remove(Connection connection)
    {
        _idleManager.Remove(connection);
    }

    public void Touch(Connection connection)
    {
        _idleManager.Touch(connection);
    }

    public IList<Connection> GetIdleConnections()
    {
        return _idleManager.GetIdleConnections();
    }

    public int GetNextTimeout()
    {
        return _idleManager.GetNextTimeout();
    }
}
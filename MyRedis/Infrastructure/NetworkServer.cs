using System.Net;
using System.Net.Sockets;
using MyRedis.Abstractions;
using MyRedis.Core;

namespace MyRedis.Infrastructure;

/// <summary>
/// Handles network operations and connection lifecycle
/// Single Responsibility: Network I/O and connection management
/// </summary>
public class NetworkServer
{
    private readonly Socket _listener;
    private readonly List<Socket> _allSockets = new();
    private readonly Dictionary<Socket, Connection> _connections = new();
    private readonly IConnectionManager _connectionManager;

    public NetworkServer(IConnectionManager connectionManager, int port = 6379)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));

        // Initialize socket
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        
        // Set socket options
        _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        
        // Bind & Listen
        _listener.Bind(new IPEndPoint(IPAddress.Any, port));
        _listener.Listen(128);
        
        // Set to non-blocking mode
        _listener.Blocking = false;
        
        _allSockets.Add(_listener);
        Console.WriteLine($"[Server] Listening on {IPAddress.Any}:{port}");
    }

    /// <summary>
    /// Performs one iteration of the network event loop
    /// </summary>
    /// <param name="timeoutMicroseconds">Timeout for select operation</param>
    /// <returns>List of connections that received data</returns>
    public IList<(Connection connection, int bytesRead)> ProcessNetworkEvents(int timeoutMicroseconds)
    {
        var readList = new List<Socket>(_allSockets);
        var errorList = new List<Socket>(_allSockets);

        Socket.Select(readList, null, errorList, timeoutMicroseconds);

        var results = new List<(Connection, int)>();

        foreach (var socket in readList)
        {
            if (socket == _listener)
            {
                HandleAccept();
            }
            else
            {
                var bytesRead = HandleRead(socket);
                if (bytesRead > 0 && _connections.TryGetValue(socket, out var connection))
                {
                    results.Add((connection, bytesRead));
                }
            }
        }

        return results;
    }

    private void HandleAccept()
    {
        try
        {
            var client = _listener.Accept();
            client.Blocking = false;

            _allSockets.Add(client);
            var connection = new Connection(client);
            _connections[client] = connection;
            _connectionManager.Add(connection);

            Console.WriteLine($"[New Conn] {client.RemoteEndPoint}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Accept Error] {ex.Message}");
        }
    }

    private int HandleRead(Socket clientSocket)
    {
        if (!_connections.TryGetValue(clientSocket, out var conn))
            return 0;

        _connectionManager.Touch(conn);

        try
        {
            var bytesRead = clientSocket.Receive(
                conn.ReadBuffer,
                conn.BytesRead,
                conn.ReadBuffer.Length - conn.BytesRead,
                SocketFlags.None);

            if (bytesRead == 0)
            {
                HandleDisconnect(clientSocket);
                return 0;
            }

            conn.BytesRead += bytesRead;
            return bytesRead;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            HandleDisconnect(clientSocket);
            return 0;
        }
    }

    private void HandleDisconnect(Socket socket)
    {
        if (_connections.TryGetValue(socket, out var conn))
        {
            Console.WriteLine($"[Disconnected] {socket.RemoteEndPoint}");
            _connectionManager.Remove(conn);
            conn.Close();
            _connections.Remove(socket);
        }

        _allSockets.Remove(socket);
    }

    /// <summary>
    /// Closes idle connections
    /// </summary>
    /// <param name="idleConnections">List of connections to close</param>
    public void CloseIdleConnections(IList<Connection> idleConnections)
    {
        foreach (var conn in idleConnections)
        {
            Console.WriteLine($"[Idle] Closing idle connection {conn.Socket.RemoteEndPoint}");
            HandleDisconnect(conn.Socket);
        }
    }
}
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer.Network
{
    public class MultiStructureTcpServer : IDisposable
    {
        private readonly Dictionary<int, TcpListener> _listeners = new Dictionary<int, TcpListener>();
        private readonly Dictionary<int, IProtocolHandler> _protocolHandlers = new Dictionary<int, IProtocolHandler>();
        private readonly Dictionary<int, IPacketHeader> _packetStructures = new Dictionary<int, IPacketHeader>();
        private readonly Dictionary<Guid, EnhancedClientSession> _clients = new Dictionary<Guid, EnhancedClientSession>();
        private readonly Dictionary<Guid, int> _clientPorts = new Dictionary<Guid, int>();
        private readonly FlexiblePacketProcessor _packetProcessor = new FlexiblePacketProcessor();
        private readonly object _clientsLock = new object();
        private CancellationTokenSource _cancellationTokenSource;

        public bool IsRunning { get; private set; }

        
        public void Start(Dictionary<int, (IProtocolHandler handler, IPacketHeader structure)> portConfigs)
        {
            if (IsRunning) return;

            _cancellationTokenSource = new CancellationTokenSource();
            IsRunning = true;

            foreach (var config in portConfigs)
            {
                int port = config.Key;
                var (handler, structure) = config.Value;

                var listener = new TcpListener(IPAddress.Any, port);
                _listeners[port] = listener;
                _protocolHandlers[port] = handler;
                _packetStructures[port] = structure;


                listener.Start();


                
                _ = AcceptConnectionsAsync(port, listener, _cancellationTokenSource.Token);

                FileLogger.Log($"Server started on port {port} with {structure.GetType().Name} and {handler.ProtocolName}");
            }
        }

        
        private async Task AcceptConnectionsAsync(int port, TcpListener listener, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var tcpClient = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    var packetStructure = _packetStructures[port];
                    var session = new EnhancedClientSession(
                        tcpClient,
                        packetStructure,
                        port);

                    
                    _packetProcessor.SetClientPacketStructure(session.SessionId, packetStructure);

                    lock (_clientsLock)
                    {
                        _clients[session.SessionId] = session;
                        _clientPorts[session.SessionId] = port;
                    }

                    
                    if (_protocolHandlers.TryGetValue(port, out IProtocolHandler handler))
                    {
                        await handler.OnClientConnected(session);
                    }

                    
                    _ = ProcessClientAsync(port, session, cancellationToken);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"Error accepting connection on port {port}: {ex.Message}");
                }
            }
        }

        
        private async Task ProcessClientAsync(int port, EnhancedClientSession session, CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];

            try
            {
                while (!cancellationToken.IsCancellationRequested && session.TcpClient.Connected)
                {
                    int bytesRead = await session.Stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);

                    if (bytesRead == 0)
                    {
                        break;
                    }

                    
                    var packets = _packetProcessor.ProcessReceivedData(session.SessionId, buffer, bytesRead);

                    
                    if (_protocolHandlers.TryGetValue(port, out IProtocolHandler handler))
                    {
                        foreach (var packet in packets)
                        {
                            await handler.OnPacketReceived(session, packet);
                        }
                    }
                }
            }
            catch (InvalidDataException ex)
            {
                FileLogger.Log(
                    $"[Network] WARN: invalid packet disconnected "
                    + $"session={session.SessionId} port={port} "
                    + $"remote={GetRemoteEndPoint(session)}: {ex.Message}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"Error processing client {session.SessionId} on port {port}: {ex.Message}");
            }
            finally
            {
                
                lock (_clientsLock)
                {
                    _clients.Remove(session.SessionId);
                    _clientPorts.Remove(session.SessionId);
                }

                _packetProcessor.CleanupClient(session.SessionId);
                session.Close();

                
                if (_protocolHandlers.TryGetValue(port, out IProtocolHandler handler))
                {
                    await handler.OnClientDisconnected(session);
                }
            }
        }

        private static string GetRemoteEndPoint(EnhancedClientSession session)
        {
            try
            {
                return session?.TcpClient?.Client?.RemoteEndPoint?.ToString() ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        
        public async Task BroadcastToPortAsync(int port, byte[] data)
        {
            List<EnhancedClientSession> clients = new List<EnhancedClientSession>();

            lock (_clientsLock)
            {
                foreach (var clientId in _clientPorts.Keys)
                {
                    if (_clientPorts[clientId] == port && _clients.TryGetValue(clientId, out EnhancedClientSession client))
                    {
                        clients.Add(client);
                    }
                }
            }

            var tasks = new List<Task>();
            foreach (var client in clients)
            {
                if (client.TcpClient.Connected)
                {
                    tasks.Add(client.SendPacketAsync(data));
                }
            }

            await Task.WhenAll(tasks);
        }

        
        public ServerStatistics GetStatistics()
        {
            var stats = new ServerStatistics();

            lock (_clientsLock)
            {
                foreach (var port in _clientPorts.Values)
                {
                    if (stats.PortStats.ContainsKey(port))
                    {
                        stats.PortStats[port]++;
                    }
                    else
                    {
                        stats.PortStats[port] = 1;
                    }
                }

                stats.TotalClients = _clients.Count;
            }

            return stats;
        }

        
        public void Stop()
        {
            if (!IsRunning) return;

            _cancellationTokenSource?.Cancel();

            foreach (var listener in _listeners.Values)
            {
                listener.Stop();
            }

            lock (_clientsLock)
            {
                foreach (var client in _clients.Values)
                {
                    client.Close();
                }
                _clients.Clear();
                _clientPorts.Clear();
            }

            _listeners.Clear();
            IsRunning = false;
        }

        public void Dispose()
        {
            Stop();
            _cancellationTokenSource?.Dispose();
        }
    }

    
    public class ServerStatistics
    {
        public int TotalClients { get; set; }
        public Dictionary<int, int> PortStats { get; } = new Dictionary<int, int>();
    }
}

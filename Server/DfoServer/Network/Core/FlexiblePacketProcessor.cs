using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.Network
{
    
    public class FlexiblePacket
    {
        public IPacketHeader PacketStructure { get; set; }

        public byte[] BodyData { get; set; }

        public int TotalLength => PacketStructure.GetHeaderSize() + (BodyData?.Length ?? 0);

        
        public FlexiblePacket(IPacketHeader packetStructure, byte[] bodyData = null)
        {
            PacketStructure = packetStructure;
            BodyData = bodyData;
        }

        
        public byte[] GetBytes()
        {
            byte[] headerBytes = PacketStructure.GetBytes();
            if (BodyData == null || BodyData.Length == 0)
            {
                return headerBytes;
            }
            byte[] packetBytes = new byte[headerBytes.Length + BodyData.Length];
            Buffer.BlockCopy(headerBytes, 0, packetBytes, 0, headerBytes.Length);
            Buffer.BlockCopy(BodyData, 0, packetBytes, headerBytes.Length, BodyData.Length);
            return packetBytes;
        }

        
        public T GetHeader<T>() where T : struct
        {
            if (PacketStructure is T typedHeader)
                return typedHeader;

            throw new InvalidCastException($"Cannot cast header to {typeof(T).Name}");
        }
    }

    
    public class FlexiblePacketProcessor
    {
        internal const int MaxPacketLength = 1024 * 1024;
        internal const int MaxBufferedBytesPerClient = 2 * 1024 * 1024;
        private readonly object _lockObject = new object();
        private readonly Dictionary<Guid, byte[]> _receiveBuffers = new Dictionary<Guid, byte[]>();
        private readonly Dictionary<Guid, IPacketHeader> _clientPacketStructures = new Dictionary<Guid, IPacketHeader>();

        
        public void SetClientPacketStructure(Guid clientId, IPacketHeader packetStructure)
        {
            lock (_lockObject)
            {
                _clientPacketStructures[clientId] = packetStructure;
            }
        }

        
        public List<FlexiblePacket> ProcessReceivedData(Guid clientId, byte[] receivedData, int bytesRead)
        {
            if (receivedData == null)
                throw new ArgumentNullException(nameof(receivedData));
            if (bytesRead < 0 || bytesRead > receivedData.Length)
                throw new ArgumentOutOfRangeException(nameof(bytesRead));

            lock (_lockObject)
            {
                List<FlexiblePacket> packets = new List<FlexiblePacket>();

                if (!_clientPacketStructures.TryGetValue(clientId, out IPacketHeader basePacketStructure))
                {
                    throw new InvalidOperationException($"No packet structure defined for client {clientId}");
                }

                
                if (!_receiveBuffers.TryGetValue(clientId, out byte[] buffer))
                {
                    buffer = new byte[0];
                    _receiveBuffers[clientId] = buffer;
                }

                
                if ((long)buffer.Length + bytesRead > MaxBufferedBytesPerClient)
                {
                    throw new InvalidDataException(
                        $"Receive buffer limit exceeded for client {clientId}: "
                        + $"buffered={buffer.Length}, incoming={bytesRead}, "
                        + $"maximum={MaxBufferedBytesPerClient}.");
                }

                byte[] newBuffer = new byte[buffer.Length + bytesRead];
                Buffer.BlockCopy(buffer, 0, newBuffer, 0, buffer.Length);
                Buffer.BlockCopy(receivedData, 0, newBuffer, buffer.Length, bytesRead);

                int offset = 0;

                
                int headerSize = basePacketStructure.GetHeaderSize();
                if (headerSize <= 0 || headerSize > MaxPacketLength)
                    throw new InvalidDataException($"Invalid packet header size {headerSize} for client {clientId}.");

                while (offset <= newBuffer.Length - headerSize)
                {
                    
                    byte[] headerData = new byte[headerSize];
                    Buffer.BlockCopy(newBuffer, offset, headerData, 0, headerSize);

                    
                    IPacketHeader packetHeader = CreateHeaderInstance(basePacketStructure);
                    packetHeader.ParseHeader(headerData);

                    
                    uint packetLength = packetHeader.GetPacketLength();
                    if (packetLength < headerSize || packetLength > MaxPacketLength)
                    {
                        throw new InvalidDataException(
                            $"Invalid packet length {packetLength} for client {clientId}; "
                            + $"expected {headerSize}..{MaxPacketLength} bytes.");
                    }
                    int frameLength = (int)packetLength;

                    if (newBuffer.Length - offset >= frameLength)
                    {
                        int bodyLength = frameLength - headerSize;
                        byte[] bodyData = null;

                        if (bodyLength > 0)
                        {
                            bodyData = new byte[bodyLength];
                            Buffer.BlockCopy(newBuffer, offset + headerSize, bodyData, 0, bodyLength);
                        }

                        var packet = new FlexiblePacket(packetHeader, bodyData);
                        packets.Add(packet);
                        offset += frameLength;
                    }
                    else
                    {
                        
                        break;
                    }
                }

                
                if (offset < newBuffer.Length)
                {
                    byte[] remainingData = new byte[newBuffer.Length - offset];
                    Buffer.BlockCopy(newBuffer, offset, remainingData, 0, remainingData.Length);
                    _receiveBuffers[clientId] = remainingData;
                }
                else
                {
                    _receiveBuffers[clientId] = new byte[0];
                }

                return packets;
            }
        }

        
        private IPacketHeader CreateHeaderInstance(IPacketHeader baseStructure)
        {
            if (baseStructure is ChannelPacketHeader)
            {
                return new ChannelPacketHeader();
            }
            else if (baseStructure is GamePacketHeader)
            {
                return new GamePacketHeader();
            }
            else
            {
                
                return (IPacketHeader)Activator.CreateInstance(baseStructure.GetType());
            }
        }

        
        public void CleanupClient(Guid clientId)
        {
            lock (_lockObject)
            {
                _receiveBuffers.Remove(clientId);
                _clientPacketStructures.Remove(clientId);
            }
        }
    }
}

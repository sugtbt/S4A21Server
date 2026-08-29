using DfoServer.Network;
using System;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class PacketFramingBoundsSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== PACKET_FRAMING_BOUNDS selftest ===");
            var failures = 0;

            VerifyValidFragmentedAndCoalescedPackets(ref failures);
            VerifyInvalidLengths(ref failures);
            VerifyPerClientBufferLimit(ref failures);

            Console.WriteLine(
                failures == 0
                    ? "PACKET_FRAMING_BOUNDS selftest passed."
                    : $"PACKET_FRAMING_BOUNDS selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyValidFragmentedAndCoalescedPackets(ref int failures)
        {
            var processor = new FlexiblePacketProcessor();
            var clientId = Guid.NewGuid();
            processor.SetClientPacketStructure(clientId, new GamePacketHeader());
            var first = BuildGamePacket(3, seed: 0x10);
            var second = BuildGamePacket(5, seed: 0x20);

            var firstHalf = first.Take(first.Length / 2).ToArray();
            var secondHalf = first.Skip(first.Length / 2).Concat(second).ToArray();
            var pending = processor.ProcessReceivedData(
                clientId,
                firstHalf,
                firstHalf.Length);
            var packets = processor.ProcessReceivedData(
                clientId,
                secondHalf,
                secondHalf.Length);

            Check(
                "valid fragmented and coalesced packets preserve frame boundaries",
                pending.Count == 0
                && packets.Count == 2
                && packets[0].BodyData.SequenceEqual(new byte[] { 0x10, 0x11, 0x12 })
                && packets[1].BodyData.SequenceEqual(new byte[] { 0x20, 0x21, 0x22, 0x23, 0x24 }),
                ref failures);
        }

        private static void VerifyInvalidLengths(ref int failures)
        {
            var headerSize = ((IPacketHeader)new GamePacketHeader()).GetHeaderSize();
            Check(
                "zero packet length is rejected",
                RejectsDeclaredLength(0),
                ref failures);
            Check(
                "packet length below the header is rejected",
                RejectsDeclaredLength((uint)(headerSize - 1)),
                ref failures);
            Check(
                "packet length above the maximum is rejected",
                RejectsDeclaredLength((uint)FlexiblePacketProcessor.MaxPacketLength + 1),
                ref failures);
        }

        private static void VerifyPerClientBufferLimit(ref int failures)
        {
            var processor = new FlexiblePacketProcessor();
            var clientId = Guid.NewGuid();
            processor.SetClientPacketStructure(clientId, new GamePacketHeader());
            var oversizedInput = new byte[FlexiblePacketProcessor.MaxBufferedBytesPerClient + 1];

            Check(
                "per-client receive buffer limit is enforced before concatenation",
                ThrowsInvalidData(() => processor.ProcessReceivedData(
                    clientId,
                    oversizedInput,
                    oversizedInput.Length)),
                ref failures);
        }

        private static bool RejectsDeclaredLength(uint packetLength)
        {
            var processor = new FlexiblePacketProcessor();
            var clientId = Guid.NewGuid();
            processor.SetClientPacketStructure(clientId, new GamePacketHeader());
            var header = (IPacketHeader)new GamePacketHeader
            {
                cmd = 0,
                type = 0,
                length = packetLength,
            };
            var bytes = header.GetBytes();
            return ThrowsInvalidData(() => processor.ProcessReceivedData(
                clientId,
                bytes,
                bytes.Length));
        }

        private static bool ThrowsInvalidData(Action action)
        {
            try
            {
                action();
                return false;
            }
            catch (InvalidDataException)
            {
                return true;
            }
        }

        private static byte[] BuildGamePacket(int bodyLength, byte seed)
        {
            var header = (IPacketHeader)new GamePacketHeader
            {
                cmd = 0,
                type = 0x04DD,
                length = (uint)(((IPacketHeader)new GamePacketHeader()).GetHeaderSize() + bodyLength),
            };
            var headerBytes = header.GetBytes();
            var packet = new byte[headerBytes.Length + bodyLength];
            Buffer.BlockCopy(headerBytes, 0, packet, 0, headerBytes.Length);
            for (var i = 0; i < bodyLength; i++)
                packet[headerBytes.Length + i] = (byte)(seed + i);
            return packet;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}

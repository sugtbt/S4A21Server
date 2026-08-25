using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using PvfLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.Game.Inventory
{
    internal static class PetCreatureScript
    {
        private const int BodyOccurrenceIndex = 0;

        private static readonly PetCreatureScriptEntry MissingEntry =
            new PetCreatureScriptEntry(0, 0, null, null, new List<string>());
        private static readonly Lazy<CreatureScriptIndex> CreatureIndex =
            new Lazy<CreatureScriptIndex>(LoadCreatureIndex);
        private static readonly ConcurrentDictionary<int, PetCreatureScriptEntry> Entries =
            new ConcurrentDictionary<int, PetCreatureScriptEntry>();

        internal static byte[] BuildNotiBody(byte mode, ushort senderUniqueId, byte serverGroup, byte[] messageBytes)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(mode);
            writer.WriteUInt16(senderUniqueId);
            writer.WriteByte(serverGroup);
            writer.WriteDstr(messageBytes ?? Array.Empty<byte>());
            return writer.ToArray();
        }

        internal static bool TryParseMessageRequest(byte[] body, out PetCreatureScriptMessageRequest request)
        {
            request = null;
            if (body == null || body.Length < 11)
                return false;

            var mode = body[0];
            var targetUniqueId = BitConverter.ToUInt16(body, 1);
            var characterId = BitConverter.ToUInt32(body, 3);
            var messageLength = BitConverter.ToInt32(body, 7);
            if (messageLength < 0 || messageLength > 256 || body.Length < 11 + messageLength)
                return false;

            var messageBytes = new byte[messageLength];
            if (messageLength > 0)
                Buffer.BlockCopy(body, 11, messageBytes, 0, messageLength);

            var offset = 11 + messageLength;
            if (mode == 1 || mode == 7)
            {
                if (body.Length < offset + 4)
                    return false;

                var nameLength = BitConverter.ToInt32(body, offset);
                if (nameLength < 0 || nameLength > 30 || body.Length < offset + 4 + nameLength)
                    return false;
            }

            request = new PetCreatureScriptMessageRequest(
                mode,
                targetUniqueId,
                characterId,
                messageBytes);
            return true;
        }

        internal static bool TryBuildWelcomeBody(int itemTemplateId, int characterId, out byte[] body)
        {
            body = null;
            if (characterId <= 0 || itemTemplateId <= 0)
                return false;

            if (TryBuildWelcomeMessage(itemTemplateId, characterId, out var message))
            {
                body = BuildNotiBody(
                    mode: 3,
                    senderUniqueId: (ushort)characterId,
                    serverGroup: 0,
                    messageBytes: message.MessageBytes);

                FileLogger.Log(
                    $"[PetCreatureScript] welcome build cid={characterId} item=0x{message.ItemTemplateId:X8} " +
                    $"creature={message.CreatureId} script={message.ScriptFilePath} len={message.MessageBytes.Length}");
                return true;
            }

            FileLogger.Log($"[PetCreatureScript] welcome empty cid={characterId} item=0x{itemTemplateId:X8}: no ambient room line");
            return false;
        }

        private static bool TryBuildWelcomeMessage(int itemTemplateId, int characterId, out PetCreatureWelcomeMessage message)
        {
            message = default(PetCreatureWelcomeMessage);
            if (itemTemplateId <= 0)
                return false;

            var entry = Entries.GetOrAdd(itemTemplateId, ResolveEntry);
            if (entry == MissingEntry || entry.RoomAmbientLines.Count == 0)
                return false;

            var seed = unchecked((uint)(characterId * 397) ^ (uint)itemTemplateId ^ (uint)entry.CreatureId);
            var line = entry.RoomAmbientLines[(int)(seed % (uint)entry.RoomAmbientLines.Count)];
            if (string.IsNullOrWhiteSpace(line))
                return false;

            message = new PetCreatureWelcomeMessage(
                itemTemplateId,
                entry.CreatureId,
                entry.CreatureFilePath,
                entry.ScriptFilePath,
                ClientTextEncoding.GetBytes(line));
            return true;
        }

        private static CreatureScriptIndex LoadCreatureIndex()
        {
            var pathById = new Dictionary<int, string>();
            var idByStem = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var creatureList = LstFile.Parse(ReadPvfText("Creature/Creature.lst", "creature/creature.lst"));
                foreach (var creature in creatureList.Entries)
                {
                    if (creature == null || creature.Id <= 0 || string.IsNullOrWhiteSpace(creature.FilePath))
                        continue;

                    pathById[creature.Id] = creature.FilePath;
                    var stem = Path.GetFileNameWithoutExtension(creature.FilePath);
                    if (!string.IsNullOrWhiteSpace(stem) && !idByStem.ContainsKey(stem))
                        idByStem[stem] = creature.Id;
                }

                FileLogger.Log($"[PetCreatureScript] loaded Creature.lst index entries={pathById.Count}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[PetCreatureScript] Creature.lst index load failed: {ex.Message}");
            }

            return new CreatureScriptIndex(pathById, idByStem);
        }

        private static PetCreatureScriptEntry ResolveEntry(int itemTemplateId)
        {
            try
            {
                var equipment = ItemMetadataResolver.GetEquipmentEntry(itemTemplateId);
                if (equipment == null || equipment.Id <= 0 || string.IsNullOrWhiteSpace(equipment.FilePath))
                    return MissingEntry;

                var normalizedPath = equipment.FilePath.Replace('\\', '/');
                if (!normalizedPath.StartsWith("creature/", StringComparison.OrdinalIgnoreCase))
                    return MissingEntry;

                if (!ItemMetadataResolver.TryLoadEquipmentFile(equipment.Id, out var equipmentFile)
                    || !IsCreatureEquipment(equipmentFile))
                    return MissingEntry;

                var stem = Path.GetFileNameWithoutExtension(equipment.FilePath);
                var index = CreatureIndex.Value;
                if (string.IsNullOrWhiteSpace(stem)
                    || !index.CreatureIdByStem.TryGetValue(stem, out var creatureId)
                    || !index.CreaturePathById.TryGetValue(creatureId, out var creatureFilePath))
                    return MissingEntry;

                var scriptFilePath = ResolveScriptFilePath(creatureFilePath);
                if (string.IsNullOrWhiteSpace(scriptFilePath))
                    return MissingEntry;

                var lines = LoadRoomAmbientLines(scriptFilePath);
                return lines.Count == 0
                    ? MissingEntry
                    : new PetCreatureScriptEntry(
                        equipment.Id,
                        creatureId,
                        creatureFilePath,
                        scriptFilePath,
                        lines);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[PetCreatureScript] resolve skipped item=0x{itemTemplateId:X8}: {ex.Message}");
                return MissingEntry;
            }
        }

        private static string ResolveScriptFilePath(string creatureFilePath)
        {
            if (string.IsNullOrWhiteSpace(creatureFilePath))
                return null;

            var stem = Path.GetFileNameWithoutExtension(creatureFilePath);
            if (string.IsNullOrWhiteSpace(stem))
                return null;

            foreach (var candidate in new[]
            {
                "creature/script/" + stem.ToLowerInvariant() + ".wrd",
                "Creature/Script/" + stem.ToLowerInvariant() + ".wrd",
                "creature/script/" + stem + ".wrd",
                "Creature/Script/" + stem + ".wrd",
            })
            {
                try
                {
                    PvfArchiveAccessor.ReadText(candidate);
                    return candidate;
                }
                catch
                {
                    // PVF 路径大小写不稳定，继续尝试下一种写法。
                }
            }

            return null;
        }

        private static List<string> LoadRoomAmbientLines(string scriptFilePath)
        {
            try
            {
                var text = PvfArchiveAccessor.ReadText(scriptFilePath);
                var ambient = ExtractSection(text, "[ambient]", "[/ambient]");
                var roomList = ExtractSection(ambient, "[room list]", "[/room list]");
                return ExtractBacktickStrings(roomList);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[PetCreatureScript] welcome script skipped path={scriptFilePath}: {ex.Message}");
                return new List<string>();
            }
        }

        private static string ReadPvfText(params string[] paths)
        {
            Exception last = null;
            foreach (var path in paths)
            {
                try
                {
                    return PvfArchiveAccessor.ReadText(path);
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            throw last ?? new FileNotFoundException("PVF creature script not found.");
        }

        private static string ExtractSection(string text, string startTag, string endTag)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var start = text.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return string.Empty;
            start += startTag.Length;

            var end = text.IndexOf(endTag, start, StringComparison.OrdinalIgnoreCase);
            return end <= start
                ? string.Empty
                : text.Substring(start, end - start);
        }

        private static List<string> ExtractBacktickStrings(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text))
                return result;

            var offset = 0;
            while (offset < text.Length)
            {
                var start = text.IndexOf('`', offset);
                if (start < 0)
                    break;

                var end = text.IndexOf('`', start + 1);
                if (end < 0)
                    break;

                var line = text.Substring(start + 1, end - start - 1).Trim();
                if (line.Length > 0)
                    result.Add(line);
                offset = end + 1;
            }

            return result;
        }

        private static bool IsCreatureEquipment(EquipmentFile equipment)
        {
            var type = equipment?.EquipmentType;
            return !string.IsNullOrWhiteSpace(type)
                && type.IndexOf("[creature]", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    internal sealed class PetCreatureScriptMessageRequest
    {
        public PetCreatureScriptMessageRequest(
            byte mode,
            ushort targetUniqueId,
            uint characterId,
            byte[] messageBytes)
        {
            Mode = mode;
            TargetUniqueId = targetUniqueId;
            CharacterId = characterId;
            MessageBytes = messageBytes ?? Array.Empty<byte>();
        }

        public byte Mode { get; }
        public ushort TargetUniqueId { get; }
        public uint CharacterId { get; }
        public byte[] MessageBytes { get; }
    }

    internal sealed class CreatureScriptIndex
    {
        public CreatureScriptIndex(
            Dictionary<int, string> creaturePathById,
            Dictionary<string, int> creatureIdByStem)
        {
            CreaturePathById = creaturePathById ?? new Dictionary<int, string>();
            CreatureIdByStem = creatureIdByStem ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        public Dictionary<int, string> CreaturePathById { get; }
        public Dictionary<string, int> CreatureIdByStem { get; }
    }

    internal readonly struct PetCreatureWelcomeMessage
    {
        public PetCreatureWelcomeMessage(
            int itemTemplateId,
            int creatureId,
            string creatureFilePath,
            string scriptFilePath,
            byte[] messageBytes)
        {
            ItemTemplateId = itemTemplateId;
            CreatureId = creatureId;
            CreatureFilePath = creatureFilePath;
            ScriptFilePath = scriptFilePath;
            MessageBytes = messageBytes ?? Array.Empty<byte>();
        }

        public int ItemTemplateId { get; }
        public int CreatureId { get; }
        public string CreatureFilePath { get; }
        public string ScriptFilePath { get; }
        public byte[] MessageBytes { get; }
    }

    internal sealed class PetCreatureScriptEntry
    {
        public PetCreatureScriptEntry(
            int itemTemplateId,
            int creatureId,
            string creatureFilePath,
            string scriptFilePath,
            List<string> roomAmbientLines)
        {
            ItemTemplateId = itemTemplateId;
            CreatureId = creatureId;
            CreatureFilePath = creatureFilePath;
            ScriptFilePath = scriptFilePath;
            RoomAmbientLines = roomAmbientLines ?? new List<string>();
        }

        public int ItemTemplateId { get; }
        public int CreatureId { get; }
        public string CreatureFilePath { get; }
        public string ScriptFilePath { get; }
        public List<string> RoomAmbientLines { get; }
    }
}

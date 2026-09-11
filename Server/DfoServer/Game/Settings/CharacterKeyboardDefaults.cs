using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using DfoServer.GameWorld;

namespace DfoServer.Game.Settings
{
    // 无已保存键位时 01C7 的投影。普通职业读 clientonly/hotkeysystem.co，
    // 缔造者读 clientonly/hotkeysystemforcreator.co。解析方式与现网缔造者
    // 默认布局相同：文件顺序的键码，前面留 4 个槽。不在创角时写库。
    public static class CharacterKeyboardDefaults
    {
        private const byte CreatorMageJob = 10;
        private const ushort UnassignedKey = 0x86;
        private const int HeaderSlots = 4;
        private const string NormalHotkeyPvfPath = "clientonly/hotkeysystem.co";
        private const string CreatorHotkeyPvfPath = "clientonly/hotkeysystemforcreator.co";

        private static readonly Lazy<byte[]> NormalHotkeys =
            new Lazy<byte[]>(() => BuildFromPvf(NormalHotkeyPvfPath));
        private static readonly Lazy<byte[]> CreatorHotkeys =
            new Lazy<byte[]>(() => BuildFromPvf(CreatorHotkeyPvfPath));

        public static bool IsCreatorMage(byte job)
            => job == CreatorMageJob;

        // 缔造者键位体系与普通职业不同，keyType 固定为 1；其余跟随账号设置。
        public static byte ResolveKeyType(byte job, byte accountKeyType)
            => IsCreatorMage(job) ? (byte)1 : accountKeyType;

        public static byte[] BuildHotkeySlots(byte job)
            => Clone(IsCreatorMage(job) ? CreatorHotkeys.Value : NormalHotkeys.Value);

        private static byte[] BuildFromPvf(string path)
        {
            try
            {
                var values = ParseDefaultKeys(PvfArchiveAccessor.ReadText(path));
                if (values.Count == 0)
                    return Array.Empty<byte>();

                var result = new byte[(HeaderSlots + values.Count) * 2];
                for (var i = 0; i < values.Count; i++)
                    Buffer.BlockCopy(BitConverter.GetBytes(values[i]), 0, result, (HeaderSlots + i) * 2, 2);
                return result;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[CharacterKeyboardDefaults] hotkey parse failed ({path}): {ex.Message}");
                return Array.Empty<byte>();
            }
        }

        private static List<ushort> ParseDefaultKeys(string text)
        {
            var result = new List<ushort>();
            if (string.IsNullOrWhiteSpace(text))
                return result;

            foreach (Match keyBlock in Regex.Matches(
                text,
                @"\[key\]\s*`[^`]*`\s+-?\d+\s+`[^`]*`\s+`[^`]*`\s+(-?\d+)",
                RegexOptions.IgnoreCase))
            {
                if (!int.TryParse(keyBlock.Groups[1].Value, out var value))
                    continue;
                result.Add(value < 0 ? UnassignedKey : (ushort)Math.Min(ushort.MaxValue, value));
            }

            return result;
        }

        private static byte[] Clone(byte[] source)
        {
            if (source == null || source.Length == 0)
                return Array.Empty<byte>();
            var copy = new byte[source.Length];
            Buffer.BlockCopy(source, 0, copy, 0, source.Length);
            return copy;
        }
    }
}

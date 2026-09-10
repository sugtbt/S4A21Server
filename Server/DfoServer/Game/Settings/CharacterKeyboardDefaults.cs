using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using DfoServer.GameWorld;

namespace DfoServer.Game.Settings
{
    public static class CharacterKeyboardDefaults
    {
        private const byte CreatorMageJob = 10;
        private const ushort UnassignedKey = 0x86;
        public static bool IsCreatorMage(byte job) => job == CreatorMageJob;

        public static byte[] BuildHotkeySlots(byte job)
        {
            if (!IsCreatorMage(job)) return Array.Empty<byte>();
            try
            {
                var values = ParseDefaultKeys(PvfArchiveAccessor.ReadText("clientonly/hotkeysystemforcreator.co"));
                if (values.Count == 0) return Array.Empty<byte>();
                var result = new byte[(4 + values.Count) * 2];
                for (var i = 0; i < values.Count; i++)
                    Buffer.BlockCopy(BitConverter.GetBytes(values[i]), 0, result, (i + 4) * 2, 2);
                return result;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[CharacterKeyboardDefaults] creator hotkey parse failed: {ex.Message}");
                return Array.Empty<byte>();
            }
        }

        private static List<ushort> ParseDefaultKeys(string text)
        {
            var result = new List<ushort>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            foreach (Match m in Regex.Matches(text, @"\[key\]\s*`[^`]*`\s+-?\d+\s+`[^`]*`\s+`[^`]*`\s+(-?\d+)", RegexOptions.IgnoreCase))
                if (int.TryParse(m.Groups[1].Value, out var value))
                    result.Add(value < 0 ? UnassignedKey : (ushort)Math.Min(ushort.MaxValue, value));
            return result;
        }
    }
}

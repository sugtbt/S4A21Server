using System;

namespace DfoServer.Game.Settings
{
    public sealed class AccountSettings
    {
        public const int FullAvatarOptionIndex = 55;
        // 42596 实机：勾选「转职/觉醒特效」只改 00C5 idx1；USERINFO+47 特效显示跟 bit1。
        public const int VisibleGrowEffectOptionIndex = 1;
        public const int CharacterGrowEffectOptionId = 109;
        public const int CharacterFullAvatarOptionId = 126;
        public const int CharacterGrowAvatarOptionId = 130;
        public const int CharacterOptionPayloadLength = 512;
        public const int PackedMainGameOptionLength = 150;

        public const byte GrowEffectVisibleMask = 1 << 1;
        public const byte HideFullAvatarMask = 1 << 3;

        public byte[] MainGameOption { get; set; }
        public byte[] QuickchatBank0 { get; set; }
        public byte[] QuickchatBank1 { get; set; }
        public byte HotkeyKeyType { get; set; }
        public byte[] HotkeySlots { get; set; }

        public const int AccountScopedHotkeySlotCount = 1;

        public static bool TryApplyCharacterVisibilityOptions(
            byte[] mainGameOption,
            byte currentVisibleBits,
            out byte updatedVisibleBits)
        {
            updatedVisibleBits = currentVisibleBits;
            var changed = false;

            if (TryReadOption(mainGameOption, VisibleGrowEffectOptionIndex, out var growEffectVisible))
            {
                updatedVisibleBits = growEffectVisible
                    ? (byte)(updatedVisibleBits | GrowEffectVisibleMask)
                    : (byte)(updatedVisibleBits & ~GrowEffectVisibleMask);
                changed = true;
            }

            if (TryReadOption(mainGameOption, FullAvatarOptionIndex, out var fullAvatarVisible))
            {
                updatedVisibleBits = fullAvatarVisible
                    ? (byte)(updatedVisibleBits & ~HideFullAvatarMask)
                    : (byte)(updatedVisibleBits | HideFullAvatarMask);
                changed = true;
            }

            return changed;
        }

        public static byte[] CloneMainGameOptionForCharacter(byte[] source)
        {
            var length = PackedMainGameOptionLength;
            if (source != null && source.Length > length)
                length = source.Length;
            if (length < (FullAvatarOptionIndex + 1) * 2)
                length = (FullAvatarOptionIndex + 1) * 2;

            var result = new byte[length];
            if (source != null && source.Length > 0)
                Buffer.BlockCopy(source, 0, result, 0, Math.Min(source.Length, result.Length));
            return result;
        }

        public static bool TryApplyCharacterVisibilityBitsToOptions(
            byte[] mainGameOption,
            byte visibleBits)
        {
            if (mainGameOption == null)
                return false;

            var wrote = TryWriteOption(
                mainGameOption,
                VisibleGrowEffectOptionIndex,
                (visibleBits & GrowEffectVisibleMask) != 0);
            wrote = TryWriteOption(
                    mainGameOption,
                    FullAvatarOptionIndex,
                    (visibleBits & HideFullAvatarMask) == 0)
                || wrote;
            return wrote;
        }

        public static byte[] ProjectCharacterOptionBlob(byte[] saved, byte visibleBits)
        {
            var body = NormalizeCharacterOptionBlob(saved);
            WriteCharacterOptionU16(
                body,
                CharacterGrowEffectOptionId,
                (visibleBits & GrowEffectVisibleMask) != 0);
            WriteCharacterOptionU16(
                body,
                CharacterFullAvatarOptionId,
                (visibleBits & HideFullAvatarMask) == 0);
            return body;
        }

        public static byte[] NormalizeCharacterOptionBlob(byte[] saved)
        {
            var body = new byte[4 + CharacterOptionPayloadLength];
            Buffer.BlockCopy(BitConverter.GetBytes(CharacterOptionPayloadLength), 0, body, 0, 4);
            for (var i = 4; i + 1 < body.Length; i += 2)
            {
                body[i] = 0xFF;
                body[i + 1] = 0xFF;
            }

            body[4] = 1;
            body[5] = 0;

            if (saved == null || saved.Length == 0)
                return body;

            var payloadOffset = 0;
            var payloadLength = saved.Length;
            if (saved.Length >= 4)
            {
                var declared = BitConverter.ToInt32(saved, 0);
                if (declared >= 0 && declared <= saved.Length - 4 && (declared > 0 || saved.Length == 4))
                {
                    payloadOffset = 4;
                    payloadLength = declared;
                }
            }

            var copy = Math.Min(payloadLength, CharacterOptionPayloadLength);
            if (copy > 0)
                Buffer.BlockCopy(saved, payloadOffset, body, 4, copy);
            Buffer.BlockCopy(BitConverter.GetBytes(CharacterOptionPayloadLength), 0, body, 0, 4);
            return body;
        }

        private static void WriteCharacterOptionU16(byte[] body, int optionId, bool enabled)
        {
            var offset = 4 + optionId * 2;
            if (body == null || body.Length < offset + 2)
                return;
            body[offset] = enabled ? (byte)1 : (byte)0;
            body[offset + 1] = 0;
        }

        private static bool TryReadOption(byte[] mainGameOption, int optionIndex, out bool enabled)
        {
            enabled = false;
            var offset = optionIndex * 2;
            if (mainGameOption == null || mainGameOption.Length < offset + 2)
                return false;

            enabled = BitConverter.ToUInt16(mainGameOption, offset) != 0;
            return true;
        }

        private static bool TryWriteOption(byte[] mainGameOption, int optionIndex, bool enabled)
        {
            var offset = optionIndex * 2;
            if (mainGameOption == null || mainGameOption.Length < offset + 2)
                return false;

            mainGameOption[offset] = enabled ? (byte)1 : (byte)0;
            mainGameOption[offset + 1] = 0;
            return true;
        }

        public static byte[] ExtractAccountScopedHotkeySlots(byte[] hotkeys)
        {
            if (hotkeys == null)
                return null;

            var length = Math.Min(AccountScopedHotkeySlotCount * 2, hotkeys.Length);
            var result = new byte[length];
            if (length > 0)
                Buffer.BlockCopy(hotkeys, 0, result, 0, length);
            return result;
        }
    }
}

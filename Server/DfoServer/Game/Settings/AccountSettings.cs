using System;

namespace DfoServer.Game.Settings
{
    public sealed class AccountSettings
    {
        public const int FullAvatarOptionIndex = 55;
        // 00C5 idx1 <-> USERINFO0/+47 bit1：转职/觉醒特效。
        public const int VisibleGrowEffectOptionIndex = 1;
        // 00C5 idx74 <-> USERINFO0/+47 bit4：觉醒装扮勾选。00AD 第一段保持 150B。
        public const int VisibleGrowAvatarOptionIndex = 74;
        public const int CharacterGrowEffectOptionId = 109;
        public const int CharacterFullAvatarOptionId = 126;
        public const int CharacterHonorOpacityOptionId = 127;
        public const int CharacterGrowAvatarOptionId = 130;
        public const int PackedImageCategoryIndex = 3;
        public const int PackedHonorOpacityIndex = 68;
        public const int CharacterOptionPayloadLength = 512;
        public const int PackedMainGameOptionLength = 150;

        // USERINFO0/+47 与 0x0165：bit1=转职特效，bit3=隐藏全身时装，bit4=隐藏觉醒装扮。
        // 选角 type=2 display_state_bits 用 bit0/bit1，与进角色 bit4 不是同一套。
        public const byte GrowEffectVisibleMask = 1 << 1;
        public const byte HideFullAvatarMask = 1 << 3;
        public const byte HideGrowAvatarMask = 1 << 4;

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

            if (TryReadOption(mainGameOption, VisibleGrowAvatarOptionIndex, out var growAvatarVisible))
            {
                updatedVisibleBits = growAvatarVisible
                    ? (byte)(updatedVisibleBits & ~HideGrowAvatarMask)
                    : (byte)(updatedVisibleBits | HideGrowAvatarMask);
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
            if (length < (FullAvatarOptionIndex + 1) * 2)
                length = (FullAvatarOptionIndex + 1) * 2;

            var result = new byte[length];
            if (source != null && source.Length > 0)
                Buffer.BlockCopy(source, 0, result, 0, Math.Min(source.Length, result.Length));
            return result;
        }

        public static byte[] PackAccountMainGameOption(byte[] source)
        {
            var packed = CloneMainGameOptionForCharacter(source);
            ApplySelectScreenCharacterDefaults(packed);
            return packed;
        }

        public static bool ApplySelectScreenCharacterDefaults(byte[] main)
        {
            // 选角特效/觉醒装扮走 type=2。00AD 只强制全身时装可见。
            return TryWriteOption(main, FullAvatarOptionIndex, true);
        }

        public static byte[] BuildCharacterEnterGameOption(byte[] accountMain, byte visibleBits)
        {
            var packed = CloneMainGameOptionForCharacter(accountMain);
            TryApplyCharacterVisibilityBitsToOptions(packed, visibleBits);
            return packed;
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
                VisibleGrowAvatarOptionIndex,
                (visibleBits & HideGrowAvatarMask) == 0) || wrote;
            wrote = TryWriteOption(
                mainGameOption,
                FullAvatarOptionIndex,
                (visibleBits & HideFullAvatarMask) == 0) || wrote;
            return wrote;
        }

        public static byte[] ProjectCharacterOptionBlob(byte[] saved, byte visibleBits, byte[] packedMain = null)
        {
            var body = NormalizeCharacterOptionBlob(saved);
            // 0187[126] 对应查看全身时装。不写 [109]/[130]；勾选走 00C5 idx1/idx74。
            WriteCharacterOptionU16(
                body,
                CharacterFullAvatarOptionId,
                (visibleBits & HideFullAvatarMask) == 0);
            if (packedMain != null)
                WriteCharacterOptionRawU16(
                    body,
                    CharacterHonorOpacityOptionId,
                    ReadU16(packedMain, PackedHonorOpacityIndex));
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
            WriteCharacterOptionRawU16(body, optionId, enabled ? (ushort)1 : (ushort)0);
        }

        private static void WriteCharacterOptionRawU16(byte[] body, int optionId, ushort value)
        {
            var offset = 4 + optionId * 2;
            if (body == null || body.Length < offset + 2)
                return;
            body[offset] = (byte)value;
            body[offset + 1] = (byte)(value >> 8);
        }

        private static ushort ReadU16(byte[] blob, int index)
        {
            var offset = index * 2;
            if (blob == null || blob.Length < offset + 2)
                return 0;
            return BitConverter.ToUInt16(blob, offset);
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

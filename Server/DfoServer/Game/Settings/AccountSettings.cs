namespace DfoServer.Game.Settings
{
    public sealed class AccountSettings
    {
        public const int FullAvatarOptionIndex = 55;
        public const int VisibleGrowAvatarOptionIndex = 1;

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
            if (!TryReadOption(mainGameOption, FullAvatarOptionIndex, out var fullAvatarVisible)
                || !TryReadOption(mainGameOption, VisibleGrowAvatarOptionIndex, out var growAvatarVisible))
                return false;

            const byte growAvatarVisibleMask = 1 << 1;
            const byte hideFullAvatarMask = 1 << 3;

            updatedVisibleBits = growAvatarVisible
                ? (byte)(updatedVisibleBits | growAvatarVisibleMask)
                : (byte)(updatedVisibleBits & ~growAvatarVisibleMask);
            updatedVisibleBits = fullAvatarVisible
                ? (byte)(updatedVisibleBits & ~hideFullAvatarMask)
                : (byte)(updatedVisibleBits | hideFullAvatarMask);
            return true;
        }

        public static bool TryApplyCharacterVisibilityBitsToOptions(
            byte[] mainGameOption,
            byte visibleBits)
        {
            if (mainGameOption == null
                || mainGameOption.Length < (FullAvatarOptionIndex + 1) * 2)
                return false;

            const byte growAvatarVisibleMask = 1 << 1;
            const byte hideFullAvatarMask = 1 << 3;

            return TryWriteOption(
                    mainGameOption,
                    VisibleGrowAvatarOptionIndex,
                    (visibleBits & growAvatarVisibleMask) != 0)
                && TryWriteOption(
                    mainGameOption,
                    FullAvatarOptionIndex,
                    (visibleBits & hideFullAvatarMask) == 0);
        }

        private static bool TryReadOption(byte[] mainGameOption, int optionIndex, out bool enabled)
        {
            enabled = false;
            var offset = optionIndex * 2;
            if (mainGameOption == null || mainGameOption.Length < offset + 2)
                return false;

            enabled = System.BitConverter.ToUInt16(mainGameOption, offset) != 0;
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

            var length = System.Math.Min(AccountScopedHotkeySlotCount * 2, hotkeys.Length);
            var result = new byte[length];
            if (length > 0)
                System.Buffer.BlockCopy(hotkeys, 0, result, 0, length);
            return result;
        }
    }
}

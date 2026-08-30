using DfoServer.Game.Settings;
using System;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    /// 构造账号级游戏选项包体。选角前 00AD 与选角初始化 00AD 共用。
    /// 无任何已保存设置的账号不下发对应包，由客户端使用本地默认。
    public static class AccountSettingsPacketBuilder
    {
        // A21 进入选角前只发送账号当前的 00AD。无保存记录的账号不下发，由客户端使用本地默认。
        // FullAvatar(idx55) 必须开启，否则客户端会隐藏完整身体外观。
        public static byte[] BuildSelectScreenGameOption(
            AccountSettings settings,
            out byte[] persistedMain)
        {
            persistedMain = null;
            var source = settings?.MainGameOption;
            if (source == null)
                return null;

            var main = new byte[source.Length];
            Buffer.BlockCopy(source, 0, main, 0, source.Length);
            persistedMain = EnsureFullAvatarVisible(main) ? main : null;

            return BuildGameOptionBody(
                main,
                settings.QuickchatBank0 ?? Array.Empty<byte>(),
                settings.QuickchatBank1 ?? Array.Empty<byte>());
        }

        internal static bool EnsureFullAvatarVisible(byte[] main)
        {
            var offset = AccountSettings.FullAvatarOptionIndex * 2;
            if (main == null || main.Length < offset + 2)
                return false;
            if (main[offset] == 1 && main[offset + 1] == 0)
                return false;

            main[offset] = 1;
            main[offset + 1] = 0;
            return true;
        }

        public static byte[] BuildGameOptionBody(byte[] main, byte[] quick0, byte[] quick1)
        {
            var writer = new GamePacketWriter();
            WriteLengthPrefixed(writer, main);
            WriteLengthPrefixed(writer, quick0);
            WriteLengthPrefixed(writer, quick1);
            return writer.ToArray();
        }

        public static byte[] BuildHotkeyOptionBody(byte keyType, byte[] hotkeys)
        {
            hotkeys = hotkeys ?? Array.Empty<byte>();
            var body = new byte[1 + 4 + hotkeys.Length];
            body[0] = keyType;
            Buffer.BlockCopy(BitConverter.GetBytes(hotkeys.Length), 0, body, 1, 4);
            if (hotkeys.Length > 0)
                Buffer.BlockCopy(hotkeys, 0, body, 5, hotkeys.Length);
            return body;
        }

        public static byte[] BuildHotkeyOptionBody(byte keyType, IReadOnlyList<ushort> slots)
        {
            var slotCount = slots?.Count ?? 0;
            var hotkeys = new byte[slotCount * 2];
            for (var i = 0; i < slotCount; i++)
                Buffer.BlockCopy(BitConverter.GetBytes(slots[i]), 0, hotkeys, i * 2, 2);
            return BuildHotkeyOptionBody(keyType, hotkeys);
        }

        private static void WriteLengthPrefixed(GamePacketWriter writer, byte[] body)
        {
            body = body ?? Array.Empty<byte>();
            writer.WriteInt32(body.Length);
            writer.WriteBytes(body);
        }
    }
}

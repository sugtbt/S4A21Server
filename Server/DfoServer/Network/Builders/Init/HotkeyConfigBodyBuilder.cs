using System;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Settings;

namespace DfoServer.Network.Builders
{
    public sealed class HotkeyConfigBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => (ushort)NotiPacketTypeA21.HOTKEY_OPTION;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var init = snapshot.InitializationSnapshot;
            var job = snapshot.CharacterRecord?.Job ?? 0;

            // 01C7 必须恒发: 客户端换角色不会自行重置内存键位表, 跳过会让无存档
            // 角色沿用上一个选取角色的键位, 并在下一次 SAVE_GAME_OPTION_2 时固化。
            // 无存档载荷来自 PVF 默认布局, 不写库; 有存档仍按 character_hotkey_slots 投影。
            var keyType = CharacterKeyboardDefaults.ResolveKeyType(job, init.HotkeyKeyType);
            if (init.HotkeyConfigSlots.Count == 0)
            {
                body = AccountSettingsPacketBuilder.BuildHotkeyOptionBody(
                    keyType,
                    CharacterKeyboardDefaults.BuildHotkeySlots(job));
                return true;
            }

            body = AccountSettingsPacketBuilder.BuildHotkeyOptionBody(keyType, init.HotkeyConfigSlots);
            return true;
        }
    }
}

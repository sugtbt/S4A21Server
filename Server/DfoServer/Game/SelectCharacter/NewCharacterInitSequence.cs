using DfoServer.Game.Inventory;
using DfoServer.Network;
using System.Collections.Generic;

namespace DfoServer.Game.SelectCharacter
{
    public static class NewCharacterInitSequence
    {
        public static List<SelectCharacterPacketTemplate> Build()
        {
            var list = new List<SelectCharacterPacketTemplate>();

            void Raw(byte cmd, ushort type, int occ = 0)
                => list.Add(new SelectCharacterPacketTemplate
                {
                    Kind = SelectCharacterPacketTemplateKind.Raw,
                    Command = cmd, Type = type, OccurrenceIndex = occ
                });

            void Item(InventoryListType lt)
                => list.Add(new SelectCharacterPacketTemplate
                {
                    Kind = SelectCharacterPacketTemplateKind.ItemList,
                    Command = 0x00, Type = 0x000D, ItemListType = lt
                });

            // A21 进号主序列：USERINFO0 -> 基础状态 -> USERINFO1 -> 城镇状态
            // -> 0005 -> ITEM_LIST -> 0245 -> 0465/021E。
            Raw(0x01, 0x0004);                      
            Raw(0x00, (ushort)NotiPacketTypeA21.USERINFO, 0);
            Raw(0x00, 0x0166, 0);                   
            Raw(0x00, 0x0166, 1);                   
            Raw(0x00, 0x0166, 2);                   
            Raw(0x00, 0x0166, 3);                   
            Raw(0x00, 0x0166, 4);                   
            Raw(0x00, (ushort)NotiPacketTypeA21.STORY_BOOK_INFO);
            Raw(0x00, 0x0167);
            Raw(0x00, (ushort)NotiPacketTypeA21.ACCEPTABLE_QUEST_LIST);
            Raw(0x00, 0x0164);
            Raw(0x00, (ushort)NotiPacketTypeA21.SKILLINFO);
            Raw(0x00, 0x0069);
            Raw(0x00, (ushort)NotiPacketTypeA21.USERINFO, 1);
            Raw(0x00, 0x0003);
            Raw(0x00, 0x00CA);
            Raw(0x00, (ushort)NotiPacketTypeA21.DUNGEON_PERMISSION);
            Item(InventoryListType.Main);
            Item(InventoryListType.Avatar);
            Item(InventoryListType.PersonalCargo);
            Item(InventoryListType.Pet);
            Item(InventoryListType.AccountCargo);
            Item(InventoryListType.GuildMedal);
            Raw(0x00, 0x0245);
            Raw(0x00, 0x0465);
            Raw(0x00, 0x021E);
            Raw(0x00, 0x0286);
            Raw(0x00, 0x00AD);
            Raw(0x00, 0x01C7);
            Raw(0x00, 0x006C);
            Raw(0x00, 0x0187);
            Raw(0x00, 0x01B9);
            Raw(0x00, 0x015F);                      
            Raw(0x00, 0x00AC);                      
            Raw(0x00, 0x00AE);                      
            Raw(0x00, 0x017B);                      
            Raw(0x00, 0x03EB, 0);
            Raw(0x00, 0x03EB, 1);
            Raw(0x00, 0x03EB, 2);
            Raw(0x00, 0x021F);                      
            Raw(0x00, (ushort)NotiPacketTypeA21.EQUIPMENT_RENTAL_LIST);
            Raw(0x00, 0x00FB);                      
            Raw(0x00, 0x00CD);                      
            Raw(0x00, (ushort)NotiPacketTypeA21.TAG_CHARACTER_INFO);
            Raw(0x00, 0x00B1);                      
            Raw(0x00, 0x036A);
            Raw(0x00, 0x03D8);                      
            Raw(0x00, 0x025B);                      
            Raw(0x00, 0x0331);                      
            Raw(0x00, (ushort)NotiPacketTypeA21.UPGRADE_CARRY_GOLD);
            Raw(0x00, 0x01EB);                      
            Raw(0x00, 0x0061);                      
            Raw(0x00, 0x0158);                      
            Raw(0x00, 0x02D5, 0);                   
            Raw(0x00, 0x02D5, 1);                   
            Raw(0x00, 0x02D5, 2);                   
            Raw(0x00, 0x01A8);                      
            Raw(0x00, 0x0009);
            Raw(0x00, 0x0344);
            Raw(0x00, 0x007C);                      
            Raw(0x00, (ushort)NotiPacketTypeA21.USERINFO, 2); // subtype 6，25B
            Raw(0x01, (ushort)CmdPacketTypeA21.MERCENARY_INFO);
            Raw(0x00, (ushort)NotiPacketTypeA21.CERA);
            // 0x0111 连发两遍（建节点 + 置显示），见 UnitedServerFriendInfoBodyBuilder 注释。
            Raw(0x00, (ushort)NotiPacketTypeA21.UNITED_SERVER_FRIEND_INFO, 0);
            Raw(0x00, (ushort)NotiPacketTypeA21.UNITED_SERVER_FRIEND_INFO, 1);
            Raw(0x00, 0x0016);
            Raw(0x00, 0x0077);   // 宠物欢迎语; 无宠物或无缓存时 builder 返回 false 跳过
            // 婚姻/双人房间回放尾段
            Raw(0x00, (ushort)NotiPacketTypeA21.WEDDING_INFO);
            Raw(0x01, (ushort)CmdPacketTypeA21.WEDDING_CHARAC);
            Raw(0x00, (ushort)NotiPacketTypeA21.DIMENSION_GATE_ENTRANCE_INFO);
            Raw(0x00, (ushort)NotiPacketTypeA21.COUPLE_ROOM, 1);

            return list;
        }
    }
}

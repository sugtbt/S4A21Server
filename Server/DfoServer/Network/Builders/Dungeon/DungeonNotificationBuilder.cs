using System;
using System.Collections.Generic;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class DungeonNotificationBuilder
    {
        // A21 sub_115CF80 reads 23 consecutive fixed reward values starting at
        // body offset 17. Offset 109 is then a variable-list count, not slot 23.
        public const int RewardSlotBlockOffset = 17;
        public const int FixedRewardSlotCount = 23;
        public const int EquipmentBonusExpSlotIndex = 20;
        public const int FirstVariableRewardCountOffset = 109;
        public const int SecondVariableRewardCountOffset = 110;
        public const int PostVariableRewardBlockOffset = 111;
        public const int ScoreBreakdownOffset = 139;
        public const int ChampionExperienceOffset = 143;
        public const int SuperChampionExperienceOffset = 147;
        public const int BossExperienceOffset = 151;
        public const int ObjectExperienceCountOffset = 155;
        public const int ObjectExperienceEntriesOffset = 159;
        public const byte NoBossMapMarkerCoordinate = 0xFF;

        // NOTI 28 (0x001C) DUNGEON_INFO
        // A21 的固定前缀从 u32 dungeonId 开始。客户端 reader 还会读取
        // 一段可变的 minimap group 列表；无图标时官方样本的组数为 0，
        // 随后的固定标记为 1，因此仍然是 32B。存在图标组时，body 按
        // 组/条目数量增长，固定标记仍位于动态组列表之后。
        public static byte[] BuildDungeonInfo(
            int dungeonId,
            byte difficulty,
            byte mazeIndex = 0,
            byte bossX = 0,
            byte bossY = 0,
            byte hellPartyRoomX = 0xFF,
            byte hellPartyRoomY = 0xFF,
            byte dungeonMode = 0,
            IReadOnlyList<IReadOnlyList<(byte, byte)>> extraPairGroups = null,
            ushort hellPartyEnabled = 0x0000,
            ushort value1 = 0x000C,
            byte value2 = 0,
            byte flagA = 0,
            uint packetSeed = 0xFFFFFFFFu,
            byte paramA = 0,
            byte paramB = 0,
            byte paramC = 0,
            byte tailFlag0 = 0,
            byte tailFlag1 = 0,
            byte tailFlag2 = 0,
            uint tailReserved = 0)
        {
            var writer = new GamePacketWriter();

            writer.WriteInt32(dungeonId);              // +0
            writer.WriteByte(difficulty);              // +4
            writer.WriteByte(mazeIndex);               // +5 selected maze index
            // BossMapPos remains a runtime fact for route selection, room
            // topology and settlement. A21 uses FF/FF as the no-marker
            // sentinel for the built-in boss minimap marker; preserve the
            // caller's resolved coordinates, including that sentinel.
            writer.WriteByte(bossX); // +6
            writer.WriteByte(bossY); // +7
            // A21 projects the selected Hell room coordinate here. The
            // official no-Hell baseline is 0/0; when Hell is enabled these
            // bytes must match the frozen HellPartyRoomInfo used by
            // START_MAP/MOVE_MAP, otherwise the minimap marker points at a
            // different room than the actual Hell MAP.
            writer.WriteByte(hellPartyEnabled > 0 ? hellPartyRoomX : (byte)0); // +8 Hell room X
            writer.WriteByte(hellPartyEnabled > 0 ? hellPartyRoomY : (byte)0); // +9 Hell room Y
            writer.WriteByte(0);                       // +10
            WriteMinimapGroups(writer, extraPairGroups);
            // A21 official Packets01/02/03 keep the post-group fixed tail
            // identical for ordinary and Hell runs. Keep the captured bytes
            // byte-for-byte; the selected Hell coordinate is carried by the
            // prefix above, not by this trailing region.
            writer.WriteByte(1);                      // fixed marker after minimap groups
            writer.WriteZeroBytes(5);                 // trailing fixed u8/reserved bytes
            writer.WriteUInt32(0xFFFFFFFFu);            // trailing sentinel
            writer.WriteZeroBytes(10);                 // remaining reserved bytes
            return writer.ToArray();
        }

        private static void WriteMinimapGroups(
            GamePacketWriter writer,
            IReadOnlyList<IReadOnlyList<(byte X, byte Y)>> groups)
        {
            // The current A21 client expects a zero group count for the
            // ordinary no-icon baseline. The following fixed marker is written
            // by BuildDungeonInfo and must remain outside this dynamic list.
            if (groups == null)
            {
                writer.WriteByte(0);
                return;
            }

            if (groups.Count > byte.MaxValue)
                throw new InvalidOperationException("A21 DUNGEON_INFO minimap group count exceeds one byte.");

            writer.WriteByte((byte)groups.Count);
            for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                var group = groups[groupIndex];
                var count = group?.Count ?? 0;
                if (count > byte.MaxValue)
                    throw new InvalidOperationException("A21 DUNGEON_INFO minimap entry count exceeds one byte.");

                writer.WriteByte((byte)count);
                for (var entryIndex = 0; entryIndex < count; entryIndex++)
                {
                    writer.WriteByte(group[entryIndex].X);
                    writer.WriteByte(group[entryIndex].Y);
                }
            }
        }

        // NOTI 679 (0x02A7) ENUM_NOTIPACKET_HELL_PARTY_MONSTER_INFO
        // 86 客户端读取：int32 count + 重复的 int32 actorIdOrKey、int32 level。
        // 当前按怪物/APC code + 对象等级发送；该包不覆盖 START_MAP 隐藏行等级。
        public static byte[] BuildHellPartyMonsterInfo(IReadOnlyList<KeyValuePair<int, int>> actorLevels)
        {
            var writer = new GamePacketWriter();
            var count = actorLevels?.Count ?? 0;
            writer.WriteInt32(count);
            for (var i = 0; i < count; i++)
            {
                writer.WriteInt32(actorLevels[i].Key);
                writer.WriteInt32(actorLevels[i].Value);
            }

            return writer.ToArray();
        }

        // NOTI 29 (0x001D) START_MAP
        public static byte[] BuildStartMap(
            Dungeon.MazeSumInfo maze,
            ushort firstMonsterSequence,
            int randomSeed = 0,
            byte layeredRoomFlag = 0,
            byte hellPartyMode = 2,
            byte unknownAfterHellPartyMode = 0,
            uint roomStateValue = 1,
            byte roomStateFlag = 1,
            byte hellPartyFogFlag = 0,
            byte partyMemberIndex = 0xFF,
            IReadOnlyList<Game.Dungeon.PassiveObjectDropEntry> extraEntries = null,
            IReadOnlyList<Game.Dungeon.RidableObjectSpawnEntry> ridableEntries = null)
        {
            var writer = new GamePacketWriter();

            writer.WriteByte((byte)maze.X);
            writer.WriteByte((byte)maze.Y);
            writer.WriteByte(layeredRoomFlag);
            writer.WriteInt32(randomSeed);
            writer.WriteByte(hellPartyMode);
            writer.WriteByte(unknownAfterHellPartyMode);
            writer.WriteInt32(unchecked((int)roomStateValue));
            writer.WriteByte(roomStateFlag);

            writer.WriteInt32(maze.Index);            // +14 A21 u32 map id
            writer.WriteByte((byte)maze.Monsters.Count);

            int normalIndex = 0;
            int apcIndex = 0;
            for (var i = 0; i < maze.Monsters.Count; i++)
            {
                var monster = maze.Monsters[i];
                bool isApc = monster.Type >= 5;
                var packetIndex = monster.PacketIndex.HasValue
                    ? monster.PacketIndex.Value
                    : (isApc ? apcIndex++ : normalIndex++);

                writer.WriteUInt16(monster.TemplateOrder);
                writer.WriteInt32(packetIndex);
                writer.WriteUInt16((ushort)(firstMonsterSequence + i));
                writer.WriteInt32(monster.Code);
                writer.WriteByte(monster.Level);
                writer.WriteByte(monster.Type);
                writer.WriteByte(monster.Flag0);
                writer.WriteByte(monster.Flag1);
                writer.WriteInt32(monster.ExtraState);
                writer.WriteByte(0);                  // A21 actor record extension
            }

            // 预生成建筑掉落，每项 19 字节。
            var extraCount = extraEntries?.Count ?? 0;
            writer.WriteByte((byte)extraCount);
            for (int i = 0; i < extraCount; i++)
            {
                var e = extraEntries[i];
                writer.WriteByte(e.ObjectIndex);     // +0  passive object index
                writer.WriteUInt16(e.GlobalSeq);     // +1  global sequence
                writer.WriteUInt32(ResolveTemplateId(e.Core, e.ItemId));        // +3  item template id
                writer.WriteUInt32(ResolveValue(e.Core, e.StackCount));    // +7  value/count
                writer.WriteUInt16(ResolveEndurance(e.Core, e.Endurance));     // +11 endurance
                writer.WriteByte(e.Core != null ? e.Core.AmplifyType : (byte)0);                 // +13 amplify type
                writer.WriteUInt16(e.Core != null ? e.Core.AmplifyValue : (ushort)0);               // +14 amplify value
                writer.WriteUInt16(0);               // +16 extended
                writer.WriteByte(0);                 // +18 extended
            }

            writer.WriteByte(hellPartyFogFlag);

            // 可骑乘对象生成列表。
            var ridableForThisRoom = new System.Collections.Generic.List<Game.Dungeon.RidableObjectSpawnEntry>();
            if (ridableEntries != null)
                foreach (var r in ridableEntries)
                    ridableForThisRoom.Add(r);

            if (ridableForThisRoom.Count > 0)
            {
                writer.WriteByte(1);                                     // 分组数量
                writer.WriteByte((byte)ridableForThisRoom.Count);        // 本组对象数量
                foreach (var r in ridableForThisRoom)
                {
                    writer.WriteInt32(r.PosX);
                    writer.WriteInt32(r.PosY);
                    writer.WriteInt32(r.ObjectIndex);
                    writer.WriteInt32(r.Faction);
                    writer.WriteInt32(r.SpawnMode);
                }
            }
            else
            {
                writer.WriteByte(0);                                     // 无可骑乘对象分组
            }

            writer.WriteByte(partyMemberIndex);

            return writer.ToArray();
        }

        public static byte[] BuildStartMapRevisit(Dungeon.MazeSumInfo maze, uint seed)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte((byte)maze.X);
            writer.WriteByte((byte)maze.Y);
            writer.WriteByte(0);                      // 分层房间标记
            writer.WriteInt32(unchecked((int)seed));
            writer.WriteByte(2);                      // A21 标准副本模式标记
            writer.WriteByte(0);                      // 深渊模式后续未知字节
            writer.WriteInt32(1);                     // 房间状态值
            writer.WriteByte(0);                      // 房间状态标记，重访为 0
            writer.WriteInt32(maze.Index);            // A21 u32 map id
            writer.WriteByte(0);                      // actor count
            writer.WriteByte(0);                      // extra entry count
            writer.WriteByte(0);                      // 深渊雾/小地图标记
            writer.WriteByte(0);                      // 可骑乘对象分组数
            writer.WriteByte(0xFF);                   // 队员索引
            return writer.ToArray();
        }

        // A21 NOTI 0x0026 body length = 3 + dropCount * 48 + 4.
        public static byte[] BuildMonsterDie(ushort monsterSeqId, IReadOnlyList<DropInfo> drops, ushort ownerActorId)
        {
            var w = new GamePacketWriter();

            w.WriteUInt16(monsterSeqId);
            var dropCount = drops?.Count ?? 0;
            w.WriteByte((byte)dropCount);

            var dropUnixSeconds = ResolveDropUnixSeconds(drops);

            for (int i = 0; i < dropCount; i++)
            {
                var d = drops[i];
                var core = d.Core;

                w.WriteUInt16(d.SceneSlot);     // +0  instanceId
                w.WriteUInt32(ResolveTemplateId(core, d.TemplateId));    // +2  itemId
                w.WriteByte(core != null ? core.Upgrade : d.UpgradeLevel);    // +6  upgrade
                // A21 的 value 字段直接投影 ItemCore.Value：
                // 装备是品级/实例值，消耗品材料是数量，宠物/时装是 UID。
                // 没有 Core 时，才退回地面记录的显示值。
                w.WriteUInt32(ResolveValue(core, d.StackCount));         // +7  value
                w.WriteUInt16(core != null ? core.Durability : d.Endurance);    // +11 durability
                w.WriteByte(core != null ? core.AmplifyType : (byte)0);          // +13 amplifyType
                w.WriteUInt16(core != null ? core.AmplifyValue : (ushort)0);     // +14 amplifyValue
                w.WriteUInt32(dropUnixSeconds);                           // +16 dropUnixSeconds
                w.WriteZeroBytes(24);                                     // +20 reserved
                w.WriteUInt16(ownerActorId);                              // +44 ownerCharacterId
                w.WriteUInt16(0);                                         // +46 reserved
            }

            // 末尾固定 4 字节
            w.WriteByte(0x00);
            w.WriteByte(0x00);
            w.WriteByte(0xFF);
            w.WriteByte(0x00);

            return w.ToArray();
        }

        private static uint ResolveTemplateId(ItemCore core, uint fallback)
        {
            return core != null && core.ItemId > 0 ? (uint)core.ItemId : fallback;
        }

        private static uint ResolveDropUnixSeconds(IReadOnlyList<DropInfo> drops)
        {
            if (drops == null)
                return 0;

            for (var index = 0; index < drops.Count; index++)
            {
                if (drops[index].DropGroupId != 0)
                    return drops[index].DropGroupId;
            }

            return 0;
        }

        private static uint ResolveValue(ItemCore core, uint fallbackValue)
        {
            return core == null
                ? fallbackValue
                : unchecked((uint)core.Value);
        }

        private static ushort ResolveEndurance(ItemCore core, ushort fallback)
        {
            return core != null ? core.Durability : fallback;
        }

        public static byte[] BuildEnableClearDungeon()
        {
            return new byte[] { 0x00 };
        }

        public static byte[] BuildLinkedDungeonInfo(
            int nextDungeonId,
            int difficulty)
        {
            var writer = new GamePacketWriter();
            writer.WriteInt32(nextDungeonId);
            writer.WriteInt32(difficulty);
            return writer.ToArray();
        }

        public static byte[] BuildTowerOfDespairClearReward(
            uint clearTimeMilliseconds,
            int floor,
            IReadOnlyList<ClearRewardGenerator.CardReward> rewards)
        {
            const int rewardSlotCount = 10;
            var writer = new GamePacketWriter();
            writer.WriteUInt32(clearTimeMilliseconds);
            writer.WriteUInt16((ushort)Math.Clamp(floor, 1, 100));
            writer.WriteByte(rewardSlotCount);
            for (var i = 0; i < rewardSlotCount; i++)
            {
                if (rewards != null
                    && i < rewards.Count
                    && rewards[i].ItemId > 0
                    && rewards[i].StackCount > 0)
                {
                    writer.WriteInt32(rewards[i].ItemId);
                    writer.WriteInt32(rewards[i].StackCount);
                }
                else
                {
                    writer.WriteInt32(-1);
                    writer.WriteInt32(0);
                }
            }

            return writer.ToArray();
        }

        // A14 SEQUENTIAL_DUNGEON_INFO reads int32 + byte + int32.
        internal static byte[] BuildSequentialDungeonInfo(
            int configKey,
            byte progressIndex,
            int routeMask)
        {
            var writer = new GamePacketWriter();
            writer.WriteInt32(configKey);
            writer.WriteByte(progressIndex);
            writer.WriteInt32(routeMask);
            return writer.ToArray();
        }

        public static byte[] BuildPlayResult(
            ushort userId,
            int clearTimeMs,
            byte rankIndex,
            byte timeBonusPoint,
            byte clientRankPoint,
            bool questMaze = false,
            bool newBestClearTime = false)
        {
            var writer = new GamePacketWriter();
            // df_game_r DisPatcher_SetPlayResult::SendResult:
            // rankIndex, clearTimeMs, timeBonusPoint, clientRankPoint,
            // then CParty::makeBestClearTimePacket.
            writer.WriteByte(rankIndex);
            writer.WriteInt32(clearTimeMs);
            writer.WriteByte(timeBonusPoint);
            writer.WriteByte(clientRankPoint);
            writer.WriteByte(questMaze ? (byte)1 : (byte)0);
            writer.WriteByte(0x01);              // member count
            writer.WriteUInt16(userId);
            writer.WriteInt32(clearTimeMs);
            writer.WriteByte(newBestClearTime ? (byte)1 : (byte)0);
            return writer.ToArray();
        }

        //
        //
        //
        // finalize (sub_1F595D0): grandTotal = expA + endValue + Σbonus
        // df_game_r CParty::clear_reward / getClearRewardBonusExp:
        // 总经验显示由通关基础经验、通关奖励字段、额外经验槽位、尾部杀怪经验共同组成。
        // 槽位：1-13 通关额外奖励，14-25 杀怪额外奖励，101-108 后置额外奖励。
        public static byte[] BuildClearDungeonReward(uint clearBaseExp, int scoreBonusExp = 0,
            uint partyClearBreakdownExp = 0,
            int avatarExp = 0, int creatureExp = 0,
            int blackDiamondExp = 0, int growthContractExp = 0,
            int monsterGrowthContractExp = 0, int adventureGroupExp = 0,
            uint monsterExp = 0, int bossExp = 0, int championExp = 0, int superChampionExp = 0,
            int freeCardGold = 0, int freeCardItemId = 0, int freeCardItemCount = 0,
            int paidCardCost = 0,
            IReadOnlyList<DungeonObjectExperienceEntry> objectExperienceEntries = null)
        {
            var w = new GamePacketWriter();
            // A12 wrote the aggregate monster EXP into its fixed tail. Current
            // A21 captures keep that field zero and project per-object EXP from
            // the u32 count/list at offsets 155/159 instead.
            _ = monsterExp;

            // === BASE BLOCK (109B = 4u32 + 1u8 + 23u32) ===
            w.WriteUInt32(clearBaseExp);
            w.WriteInt32(scoreBonusExp);
            w.WriteUInt32(partyClearBreakdownExp);
            w.WriteInt32(avatarExp);         // #4: 装扮通关奖励
            w.WriteByte(0);
            for (int i = 0; i < FixedRewardSlotCount; i++)
            {
                var value = 0;
                if (i == 2) value = blackDiamondExp;       // 槽位3: 黑钻
                else if (i == 5) value = creatureExp;       // 槽位6: 宠物通关奖励
                else if (i == 7) value = adventureGroupExp; // 槽 8：冒险团通关经验
                else if (i == 9) value = growthContractExp; // 槽位10: 成长之契约
                else if (i == 18) value = monsterGrowthContractExp; // 槽位19: 杀怪成长之契约
                w.WriteInt32(value);
            }

            // === VARIABLE REWARD LISTS ===
            // Each list is u8 count followed by count * (u32 key + u32 value).
            // Writing a u32 reward at offset 109 makes its low byte a count and
            // causes the client to consume non-existent entries.
            w.WriteByte(0);                    // first list count at offset 109
            w.WriteByte(0);                    // second list count at offset 110

            // The reader consumes two u32 values before the legacy post-base
            // block. They remain zero until their UI semantics are confirmed.
            w.WriteUInt32(0);                  // offset 111
            w.WriteUInt32(0);                  // offset 115

            // === POST-BASE (20B = 5u32) ===
            // sub_115CF80 consumes five fixed u32 values before the score
            // quartet. Their UI semantics are still unconfirmed.
            for (var i = 0; i < 5; i++)
                w.WriteInt32(0);

            // === SCORE BREAKDOWN (16B = 4u32) ===
            // The A21 reader stores these four values by index. Current A21
            // captures and the read-only A12 reference agree on the semantic
            // order: reserved, champion, super-champion, boss.
            w.WriteInt32(0);
            w.WriteInt32(Math.Max(0, championExp));
            w.WriteInt32(Math.Max(0, superChampionExp));
            w.WriteInt32(Math.Max(0, bossExp));

            // === OBJECT/MONSTER EXPERIENCE ENTRIES ===
            // A21 reads a u32 count at offset 155, not u8 plus padding.
            var entries = objectExperienceEntries
                ?? Array.Empty<DungeonObjectExperienceEntry>();
            w.WriteUInt32((uint)entries.Count);
            foreach (var entry in entries)
            {
                w.WriteUInt32(entry.ObjectKey);
                w.WriteUInt32(entry.Experience);
            }

            // The tail summary is the three score categories above. It is not
            // the aggregate per-object monster EXP.
            var specialMonsterExp = SaturatingSum(
                bossExp,
                championExp,
                superChampionExp);

            // === CARD/BUFF/TAIL (A21 fixed 115B when no bonus item) ===
            w.WriteByte(0);                    // reserved before free-card data

            byte freeCnt = (byte)(freeCardItemId > 0 ? 2 : 1);
            w.WriteByte(freeCnt);
            w.WriteInt32(0);                    // free-card item id
            w.WriteInt32(freeCardGold);
            if (freeCardItemId > 0)
            {
                w.WriteInt32(freeCardItemId);
                w.WriteInt32(freeCardItemCount);
            }

            // Seven fixed 9B card-seat entries: flag + item id + count.
            for (var i = 0; i < 7; i++)
            {
                w.WriteByte(1);
                w.WriteInt32(0);
                w.WriteInt32(0);
            }

            w.WriteInt32(Math.Max(0, paidCardCost));

            for (int i = 0; i < 8; i++)
                w.WriteByte(0);
            for (int i = 0; i < 8; i++)
                w.WriteByte(0);

            w.WriteInt32(0);                // tail card item id
            w.WriteByte(0);                 // end flag A
            w.WriteByte(0);                 // end flag B
            w.WriteUInt32(0);               // A21 sample tail monster-exp field
            w.WriteUInt32((uint)specialMonsterExp); // reserved/summary experience field
            for (var i = 0; i < 8; i++)
                w.WriteByte(0);

            return w.ToArray();
        }

        private static int SaturatingSum(int first, int second, int third)
        {
            var value = (long)Math.Max(0, first)
                + Math.Max(0, second)
                + Math.Max(0, third);
            return value >= int.MaxValue ? int.MaxValue : (int)value;
        }
    }
}

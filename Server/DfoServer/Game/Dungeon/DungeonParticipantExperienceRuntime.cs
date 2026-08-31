using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    public readonly struct DungeonObjectExperienceEntry
    {
        public DungeonObjectExperienceEntry(uint objectKey, uint experience)
        {
            ObjectKey = objectKey;
            Experience = experience;
        }

        public uint ObjectKey { get; }
        public uint Experience { get; }
    }

    internal readonly struct DungeonParticipantExperienceSnapshot
    {
        internal DungeonParticipantExperienceSnapshot(
            uint monsterBaseExperience,
            uint monsterGrowthContractBonusExperience,
            uint bossBaseExperience,
            uint championBaseExperience,
            uint superChampionBaseExperience,
            uint namedMonsterBaseExperience,
            IReadOnlyList<DungeonObjectExperienceEntry> objectExperienceEntries = null,
            uint monsterEquipmentBonusExperience = 0)
        {
            MonsterBaseExperience = monsterBaseExperience;
            MonsterGrowthContractBonusExperience =
                monsterGrowthContractBonusExperience;
            MonsterEquipmentBonusExperience = monsterEquipmentBonusExperience;
            BossBaseExperience = bossBaseExperience;
            ChampionBaseExperience = championBaseExperience;
            SuperChampionBaseExperience = superChampionBaseExperience;
            NamedMonsterBaseExperience = namedMonsterBaseExperience;
            ObjectExperienceEntries = objectExperienceEntries
                ?? Array.Empty<DungeonObjectExperienceEntry>();
        }

        internal uint MonsterBaseExperience { get; }
        internal uint MonsterGrowthContractBonusExperience { get; }
        internal uint MonsterEquipmentBonusExperience { get; }
        internal uint MonsterTotalExperience => AddSaturating(
            MonsterBaseExperience,
            AddSaturating(
                MonsterGrowthContractBonusExperience,
                MonsterEquipmentBonusExperience));
        internal uint BossBaseExperience { get; }
        internal uint ChampionBaseExperience { get; }
        internal uint SuperChampionBaseExperience { get; }
        internal uint NamedMonsterBaseExperience { get; }
        internal IReadOnlyList<DungeonObjectExperienceEntry>
            ObjectExperienceEntries { get; }

        private static uint AddSaturating(uint left, uint right)
        {
            var sum = (ulong)left + right;
            return sum >= uint.MaxValue ? uint.MaxValue : (uint)sum;
        }
    }

    // Per-participant awarded EXP ledger. World kill counts remain on
    // DungeonInstance; this runtime only records this player's projection.
    internal sealed class DungeonParticipantExperienceRuntime
    {
        private DungeonParticipantExperienceBonusSnapshot _bonusSnapshot =
            DungeonParticipantExperienceBonusSnapshot.None;
        private bool _bonusSnapshotFrozen;
        private readonly List<DungeonObjectExperienceEntry>
            _objectExperienceEntries = new List<DungeonObjectExperienceEntry>();

        internal uint MonsterBaseExperience { get; private set; }
        internal uint MonsterGrowthContractBonusExperience { get; private set; }
        internal uint MonsterEquipmentBonusExperience { get; private set; }
        // 纹章加成的小数零头(decimal 避免双精度漂移吞掉进位)。
        // 逐只 floor 会在等级惩罚的小基数下吞掉全部加成,
        // 零头带进下一只, 整局合计 ≈ 基础总额 × 百分比。
        private decimal _equipmentBonusCarry;
        internal uint MonsterTotalExperience => AddSaturating(
            MonsterBaseExperience,
            AddSaturating(
                MonsterGrowthContractBonusExperience,
                MonsterEquipmentBonusExperience));
        internal uint BossBaseExperience { get; private set; }
        internal uint ChampionBaseExperience { get; private set; }
        internal uint SuperChampionBaseExperience { get; private set; }
        internal uint NamedMonsterBaseExperience { get; private set; }

        internal bool TryFreezeBonusSnapshot(
            DungeonParticipantExperienceBonusSnapshot snapshot)
        {
            if (_bonusSnapshotFrozen || !snapshot.IsCaptured)
                return false;

            _bonusSnapshot = snapshot;
            _bonusSnapshotFrozen = true;
            return true;
        }

        internal DungeonParticipantExperienceBonusSnapshot
            CaptureBonusSnapshot() => _bonusSnapshot;

        internal bool TryFreezeStoryExperienceProfile(
            int ratePercent,
            int experienceDifficulty)
        {
            if (!_bonusSnapshotFrozen
                || _bonusSnapshot.HasStoryExperienceProfile
                || ratePercent < 0
                || experienceDifficulty < 0)
            {
                return false;
            }

            _bonusSnapshot = _bonusSnapshot
                .WithStoryExperienceProfile(
                    ratePercent,
                    experienceDifficulty);
            return true;
        }

        internal void RecordMonster(
            uint baseExperience,
            uint growthContractBonusExperience,
            bool isBoss,
            bool isChampion,
            bool isSuperChampion,
            bool isNamedMonster,
            ushort actorSequenceId = 0,
            uint equipmentBonusExperience = 0)
        {
            MonsterBaseExperience = AddSaturating(
                MonsterBaseExperience,
                baseExperience);
            MonsterGrowthContractBonusExperience = AddSaturating(
                MonsterGrowthContractBonusExperience,
                growthContractBonusExperience);
            MonsterEquipmentBonusExperience = AddSaturating(
                MonsterEquipmentBonusExperience,
                equipmentBonusExperience);
            if (isBoss)
                BossBaseExperience = AddSaturating(
                    BossBaseExperience,
                    baseExperience);
            if (isChampion)
                ChampionBaseExperience = AddSaturating(
                    ChampionBaseExperience,
                    baseExperience);
            if (isSuperChampion)
                SuperChampionBaseExperience = AddSaturating(
                    SuperChampionBaseExperience,
                    baseExperience);
            if (isNamedMonster)
                NamedMonsterBaseExperience = AddSaturating(
                    NamedMonsterBaseExperience,
                    baseExperience);
            if (actorSequenceId != 0)
                _objectExperienceEntries.Add(
                    new DungeonObjectExperienceEntry(
                        actorSequenceId,
                        baseExperience));
        }

        internal DungeonParticipantExperienceSnapshot Capture()
            => new DungeonParticipantExperienceSnapshot(
                MonsterBaseExperience,
                MonsterGrowthContractBonusExperience,
                BossBaseExperience,
                ChampionBaseExperience,
                SuperChampionBaseExperience,
                NamedMonsterBaseExperience,
                _objectExperienceEntries.ToArray(),
                MonsterEquipmentBonusExperience);

        // Compatibility setters keep existing fixture/setup APIs usable. New
        // production code records awards only through RecordMonster.
        internal void SetMonsterTotalForCompatibility(uint value)
        {
            MonsterBaseExperience = value;
            MonsterGrowthContractBonusExperience = 0;
        }

        internal void SetBossBaseForCompatibility(uint value) =>
            BossBaseExperience = value;

        internal void SetChampionBaseForCompatibility(uint value) =>
            ChampionBaseExperience = value;

        internal void SetSuperChampionBaseForCompatibility(uint value) =>
            SuperChampionBaseExperience = value;

        internal void SetNamedMonsterBaseForCompatibility(uint value) =>
            NamedMonsterBaseExperience = value;

        internal void SetGrowthContractBonusForCompatibility(uint value) =>
            MonsterGrowthContractBonusExperience = value;

        internal void SetEquipmentBonusForCompatibility(uint value) =>
            MonsterEquipmentBonusExperience = value;

        // 快捷栏纹章加成: 按百分比+进位零头计算本次应发整数, 累计由
        // RecordMonster 的 equipmentBonusExperience 完成。仅在 run.SyncRoot 下调用。
        internal uint ApplyEquipmentBonusRate(uint baseExperience, int percent)
        {
            if (baseExperience == 0 || percent <= 0)
                return 0;

            var exact = baseExperience * (percent / 100m) + _equipmentBonusCarry;
            var grant = exact >= uint.MaxValue
                ? uint.MaxValue
                : (uint)decimal.Floor(exact);
            _equipmentBonusCarry = exact - grant;
            return grant;
        }

        private static uint AddSaturating(uint left, uint right)
        {
            var sum = (ulong)left + right;
            return sum >= uint.MaxValue ? uint.MaxValue : (uint)sum;
        }
    }
}

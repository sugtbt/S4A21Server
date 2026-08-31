using System;
using DfoServer.Game.ExpertJob;

namespace DfoServer.Network.Builders.ExpertJob
{
    internal static class ExpertJobStorePacketBuilder
    {
        // CREATE_EXPERT_JOB_STORE 公共前缀末字节为 mode。
        // mode=0：把店主城镇分身转成商店。mode=1 会再生成实体并残留分身。
        // 分解与附魔共用该字段；附魔卡片资格写在 mode 之后。
        private const byte ReplaceOwnerAvatarMode = 0;

        internal static byte[] BuildCreateExpertJobNotification(ExpertJobStoreSession store)
        {
            if (store == null)
                throw new ArgumentNullException(nameof(store));
            if (store.Kind == ExpertJobStoreKind.DisjointMachine
                && store.DisjointMachine == null)
            {
                throw new ArgumentException("a disjoint machine store is required", nameof(store));
            }
            if (store.Kind == ExpertJobStoreKind.EnchantShop
                && store.Enchanter?.CardQualificationLevels == null)
            {
                throw new ArgumentException("an enchanter store is required", nameof(store));
            }
            if (store.Kind != ExpertJobStoreKind.DisjointMachine
                && store.Kind != ExpertJobStoreKind.EnchantShop)
            {
                throw new ArgumentException("an unsupported expert job store was supplied", nameof(store));
            }

            var writer = new GamePacketWriter();
            writer.WriteByte((byte)store.Kind);
            writer.WriteUInt16(store.OwnerUserId);
            writer.WriteDstr(store.NameBytes);
            writer.WriteByte(store.TownId);
            writer.WriteByte(store.AreaId);
            writer.WriteInt16(store.PositionX);
            writer.WriteInt16(store.PositionY);
            writer.WriteInt32(store.Cost);
            writer.WriteByte(ReplaceOwnerAvatarMode);
            if (store.Kind == ExpertJobStoreKind.EnchantShop)
            {
                writer.WriteByte((byte)Math.Min(
                    byte.MaxValue,
                    store.Enchanter.CardQualificationLevels.Count));
                for (var index = 0;
                    index < store.Enchanter.CardQualificationLevels.Count && index < byte.MaxValue;
                    index++)
                {
                    writer.WriteByte(store.Enchanter.CardQualificationLevels[index]);
                }
            }
            return writer.ToArray();
        }

        internal static byte[] BuildCloseNotification(ushort ownerUserId, int ownerCharacterId)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(ownerUserId);
            writer.WriteUInt16((ushort)ownerCharacterId);
            return writer.ToArray();
        }

        internal static byte[] BuildEnterSuccess(ExpertJobStoreSession store)
        {
            if (store == null)
                throw new ArgumentNullException(nameof(store));

            if (store.Kind == ExpertJobStoreKind.EnchantShop && store.Enchanter != null)
            {
                var enchantWriter = new GamePacketWriter();
                enchantWriter.WriteByte(1);
                enchantWriter.WriteByte((byte)store.Kind);
                enchantWriter.WriteUInt16(store.OwnerUserId);
                enchantWriter.WriteInt32(store.Enchanter.Endurance);
                return enchantWriter.ToArray();
            }

            if (store.Kind != ExpertJobStoreKind.DisjointMachine || store.DisjointMachine == null)
                throw new ArgumentException("an unsupported expert job store was supplied", nameof(store));

            var machine = store.DisjointMachine;
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteByte((byte)store.Kind);
            writer.WriteByte(machine.MachineGrade);
            writer.WriteInt32(store.Cost);
            writer.WriteInt32(machine.Endurance);
            return writer.ToArray();
        }

        internal static byte[] BuildDisjointSuccess(DisjointMachineOperationResult result)
        {
            var writer = new GamePacketWriter();
            var disjoint = result.DisjointResult;
            writer.WriteByte(1);
            writer.WriteInt16(disjoint.Request.TargetSlotIndex);
            writer.WriteByte((byte)disjoint.Request.ItemSpace);
            writer.WriteByte((byte)Math.Min(byte.MaxValue, disjoint.Materials.Count));
            for (var index = 0; index < disjoint.Materials.Count && index < byte.MaxValue; index++)
            {
                var material = disjoint.Materials[index];
                writer.WriteInt16(material.SlotIndex);
                writer.WriteInt32(material.ItemTemplateId);
                writer.WriteInt32(material.Count);
            }
            writer.WriteInt32(result.RequesterGold);
            writer.WriteInt32(result.Endurance);
            return writer.ToArray();
        }

        internal static byte[] BuildDisjointError(byte errorCode)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0);
            writer.WriteByte(errorCode);
            return writer.ToArray();
        }

        internal static byte[] BuildEnchantSuccess(EnchanterStoreUseResult result)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteByte(result.EnchantSucceeded ? (byte)1 : (byte)0);
            writer.WriteUInt32(result.FinalExperience);
            // The current client consumes this legacy field but does not project it.
            writer.WriteByte(0);
            writer.WriteInt32(result.Endurance);
            return writer.ToArray();
        }

        internal static byte[] BuildOwnerDisjointNotification(
            int ownerGold,
            int endurance)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt32(ownerGold);
            writer.WriteInt32(endurance);
            return writer.ToArray();
        }

        internal static byte[] BuildOwnerEnchantNotification(
            int ownerGold,
            int endurance)
        {
            var writer = new GamePacketWriter();
            writer.WriteInt32(ownerGold);
            writer.WriteInt32(endurance);
            return writer.ToArray();
        }

        internal static byte[] BuildRepairNotification(int gold, int endurance)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt32(gold);
            writer.WriteInt32(endurance);
            return writer.ToArray();
        }

        internal static byte[] BuildRepairError(byte errorCode)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0);
            writer.WriteByte(errorCode);
            return writer.ToArray();
        }

        internal static byte[] BuildUpgradeNotification(
            int gold,
            int grade,
            int endurance)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt32(gold);
            writer.WriteInt32(grade);
            writer.WriteInt32(endurance);
            return writer.ToArray();
        }

        internal static byte[] BuildUpgradeError(byte errorCode)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0);
            writer.WriteByte(errorCode);
            return writer.ToArray();
        }
    }
}

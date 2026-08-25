using DfoServer.Game.CharacterData;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Skills
{
    public sealed class BuySkillEntry
    {
        // 双字节技能编号(部分职业的技能表超过 255, 如战斗法师 使徒封印)。
        public ushort SkillIndex;
        public byte IsRefund;
        public byte Level;
    }

    public sealed class BuySkillResultEntry
    {
        public byte Slot;
        public ushort SkillId;
        public byte Level;
        public bool HasCmd;
        public readonly List<byte> CommandBytes = new List<byte>();
    }

    public sealed class BuySkillResult
    {
        public bool Success;
        public byte SkillTree;
        public ushort RemainSp;
        public ushort RemainTp;
        public readonly List<BuySkillResultEntry> Entries = new List<BuySkillResultEntry>();
        public byte ErrorCode;
        public bool ConsumedForgetRiverWater;
        public short ConsumedForgetRiverWaterSlot = -1;
        public InventoryMutationResult ConsumedForgetRiverWaterItem;
    }

    public static class BuySkillService
    {
        public static BuySkillResult Execute(SqliteCharacterProgressRepository repo, int cid, int accountId, int job, int skillTree, IList<BuySkillEntry> entries,
            int bonusSp = 0, byte level = 1, int bonusTp = 0, byte growType = 0)
        {
            Characters.CharacterStatComputer.DecodeGrowType(growType, out var firstGrow, out var secondGrow);
            var plan = BuildExecutionPlan(
                repo.LoadSkills(cid),
                repo.ConnectionString,
                accountId,
                job,
                skillTree,
                entries,
                bonusSp,
                level,
                bonusTp,
                firstGrow,
                secondGrow,
                unlimitedPoints: false);
            if (plan.Result.Success)
                repo.SaveSkillProgress(cid, plan.Snapshot);
            return plan.Result;
        }

        internal static BuySkillResult ExecuteWithRefundConsumable(
            InventoryService inventory,
            SqliteCharacterProgressRepository repo,
            int cid,
            int accountId,
            int job,
            int skillTree,
            IList<BuySkillEntry> entries,
            int bonusSp = 0,
            byte level = 1,
            int bonusTp = 0,
            byte growType = 0)
        {
            Characters.CharacterStatComputer.DecodeGrowType(
                growType,
                out var firstGrow,
                out var secondGrow);
            var plan = BuildExecutionPlan(
                repo.LoadSkills(cid),
                repo.ConnectionString,
                accountId,
                job,
                skillTree,
                entries,
                bonusSp,
                level,
                bonusTp,
                firstGrow,
                secondGrow,
                unlimitedPoints: false);
            if (!plan.Result.Success)
                return plan.Result;

            if (!plan.HasEffectiveRefund)
            {
                repo.SaveSkillProgress(cid, plan.Snapshot);
                return plan.Result;
            }

            if (inventory == null ||
                !SkillResetConsumableService.TryConsumeRefundConsumable(
                    inventory,
                    plan.RefundsOnlyTp,
                    out var consumedItemTemplateId,
                    out var consumed))
            {
                plan.Result.Success = false;
                plan.Result.ErrorCode = 3;
                return plan.Result;
            }

            repo.SaveSkillProgress(cid, plan.Snapshot);
            ApplyConsumedRefundItem(
                plan.Result,
                consumed,
                consumedItemTemplateId);
            return plan.Result;
        }

        internal static BuySkillResult ExecuteWithRefundConsumable(
            InventoryLease lease,
            SqliteCharacterProgressRepository repo,
            int cid,
            int accountId,
            int job,
            int skillTree,
            IList<BuySkillEntry> entries,
            int bonusSp = 0,
            byte level = 1,
            int bonusTp = 0,
            byte growType = 0)
        {
            if (lease?.Inventory == null || repo == null)
                return Failed(skillTree, 3);

            Characters.CharacterStatComputer.DecodeGrowType(
                growType,
                out var firstGrow,
                out var secondGrow);
            var preview = BuildExecutionPlan(
                repo.LoadSkills(cid),
                repo.ConnectionString,
                accountId,
                job,
                skillTree,
                entries,
                bonusSp,
                level,
                bonusTp,
                firstGrow,
                secondGrow,
                unlimitedPoints: false);
            if (!preview.Result.Success)
                return preview.Result;
            if (preview.HasEffectiveRefund
                && !SkillResetConsumableService.TryResolveRefundConsumable(
                    lease.Inventory,
                    preview.RefundsOnlyTp,
                    out _))
            {
                return Failed(skillTree, 3);
            }

            BuySkillResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "buy-skill-refund",
                (connection, transaction) =>
                {
                    var plan = BuildExecutionPlan(
                        repo.LoadSkills(connection, transaction, cid),
                        repo.ConnectionString,
                        accountId,
                        job,
                        skillTree,
                        entries,
                        bonusSp,
                        level,
                        bonusTp,
                        firstGrow,
                        secondGrow,
                        unlimitedPoints: false);
                    if (!plan.Result.Success)
                    {
                        committedResult = plan.Result;
                        return false;
                    }

                    InventoryMainItemConsumeResult consumed = null;
                    int consumedItemTemplateId = 0;
                    if (plan.HasEffectiveRefund
                        && !SkillResetConsumableService.TryConsumeRefundConsumable(
                            lease.Inventory,
                            plan.RefundsOnlyTp,
                            out consumedItemTemplateId,
                            out consumed))
                    {
                        committedResult = Failed(skillTree, 3);
                        return false;
                    }

                    repo.SaveSkillProgress(
                        connection,
                        transaction,
                        cid,
                        plan.Snapshot);
                    if (consumed != null)
                        ApplyConsumedRefundItem(
                            plan.Result,
                            consumed,
                            consumedItemTemplateId);
                    committedResult = plan.Result;
                    return true;
                });
            return committed && committedResult != null
                ? committedResult
                : committedResult != null && !committedResult.Success
                    ? committedResult
                    : Failed(skillTree, 3);
        }

        public static BuySkillResult ExecutePvp(
            SqlitePvpSkillRepository repo,
            int cid,
            int accountId,
            int job,
            int skillTree,
            IList<BuySkillEntry> entries,
            int bonusSp = 0,
            byte level = 1,
            int bonusTp = 0,
            byte growType = 0)
        {
            Characters.CharacterStatComputer.DecodeGrowType(
                growType,
                out var firstGrow,
                out var secondGrow);
            var snapshot = repo.LoadOrInitialize(
                cid,
                (byte)job,
                level,
                growType);
            var plan = BuildExecutionPlan(
                snapshot,
                repo.ConnectionString,
                accountId,
                job,
                skillTree,
                entries,
                bonusSp,
                level,
                bonusTp,
                firstGrow,
                secondGrow,
                unlimitedPoints: true);
            if (plan.Result.Success)
                repo.Save(cid, plan.Snapshot);
            return plan.Result;
        }

        private sealed class BuySkillExecutionPlan
        {
            public BuySkillResult Result;
            public SkillInfoSnapshot Snapshot;
            public bool HasEffectiveRefund;
            public bool RefundsOnlyTp;
        }

        private static BuySkillExecutionPlan BuildExecutionPlan(
            SkillInfoSnapshot snapshot,
            string connectionString,
            int accountId,
            int job,
            int skillTree,
            IList<BuySkillEntry> entries,
            int bonusSp,
            byte level,
            int bonusTp,
            int firstGrow,
            int secondGrow,
            bool unlimitedPoints)
        {
            int pageIdx = skillTree == 1 ? 1 : 0;
            while (snapshot.Pages.Count <= pageIdx)
                snapshot.Pages.Add(new SkillInfoPageSnapshot());
            var page = snapshot.Pages[pageIdx];

            // SP/TP 余额从已学技能列表全量派生——不再有持久化余额、不读协议镜像。
            var ledger = SkillPointLedger.Compute((byte)job, level, bonusSp, bonusTp, snapshot, pageIdx, firstGrow, secondGrow);
            int remainSp = ledger.RemainingSp;
            int remainTp = ledger.RemainingTp;

            // 等级门槛用 effectiveLevel = 角色等级 + 激活契约的 over skill 值(达人之契约+5)。
            // 首个需要门槛校验的条目才解析, 且查询走调用方传入 repo 的库(自测用临时库, 不碰生产库)。
            int effectiveLevel = -1;

            var result = new BuySkillResult { Success = true, SkillTree = (byte)skillTree };
            var hasEffectiveRefund = false;
            var hasNonTpEffectiveRefund = false;
            var occupied = new HashSet<int>();
            foreach (var e in page.Entries) occupied.Add(e.Slot);

            foreach (var req in entries)
            {
                var sd = SkillDataProvider.GetSkill(job, req.SkillIndex);
                if (sd == null) continue;

                int levels = req.Level <= 0 ? 1 : req.Level;
                var existing = page.Entries.Find(x => x.SkillId == req.SkillIndex);
                int curLevel = existing != null ? existing.Level : 0;

                if (req.IsRefund == 0)
                {
                    // 校验1: growType 等级上限门禁
                    var growtypeMaxLevel = sd.GetMaxLevelFor(firstGrow, secondGrow);
                    if (growtypeMaxLevel <= 0)
                    {
                        // PVF 可能为其他转职方向保留非零的 maximum-level 槽位，
                        // 但 fitness 明确禁止当前职业学习；必须返回失败而不是
                        // 伪造成功 ACK，让客户端把该技能当成已学。
                        result.Success = false;
                        result.ErrorCode = 18;
                        return new BuySkillExecutionPlan { Result = result, Snapshot = snapshot };
                    }

                    // Resolve the static direction cap through the same PVF owner
                    // used by skill projection. Do not read sd.MaxLevel here:
                    // [maximum level] may be a growType-indexed array whose first
                    // value is not the active profession's cap.
                    var configuredMaxLevel = sd.GetMaxLearnableLevel(
                        int.MaxValue,
                        firstGrow,
                        secondGrow);
                    if (configuredMaxLevel <= 0)
                    {
                        result.Success = false;
                        result.ErrorCode = 18;
                        return new BuySkillExecutionPlan { Result = result, Snapshot = snapshot };
                    }

                    int newLevel = curLevel + levels;
                    var effectiveMaxLevel = configuredMaxLevel;
                    if (newLevel > effectiveMaxLevel) newLevel = effectiveMaxLevel;
                    if (newLevel <= curLevel) continue;

                    // 校验2: 等级门槛 reqLevel + (targetLv-1)*interval <= characLevel
                    if (sd.RequiredLevel > 0)
                    {
                        if (effectiveLevel < 0)
                            effectiveLevel = level + Premium.PremiumEffectProvider.GetCombinedEffects(connectionString, accountId).OverSkillLevel;
                        if (newLevel > sd.GetMaxLearnableLevel(effectiveLevel, firstGrow, secondGrow))
                        {
                            result.Success = false;
                            result.ErrorCode = 18;
                            return new BuySkillExecutionPlan { Result = result, Snapshot = snapshot };
                        }
                    }

                    // 校验3: 前置技能
                    if (sd.PreRequiredSkills != null && sd.PreRequiredSkills.Length >= 2)
                    {
                        var preOk = true;
                        for (var pi = 0; pi + 1 < sd.PreRequiredSkills.Length; pi += 2)
                        {
                            var preSkillIndex = sd.PreRequiredSkills[pi];
                            var preSkillLevel = sd.PreRequiredSkills[pi + 1];
                            var preEntry = page.Entries.Find(x => x.SkillId == preSkillIndex);
                            if (preEntry == null || preEntry.Level < preSkillLevel)
                            {
                                preOk = false;
                                break;
                            }
                        }
                        if (!preOk)
                        {
                            result.Success = false;
                            result.ErrorCode = 18;
                            return new BuySkillExecutionPlan { Result = result, Snapshot = snapshot };
                        }
                    }

                    byte slotForEntry;
                    int allocatedSlot = -1;
                    if (existing != null)
                    {
                        slotForEntry = existing.Slot;
                    }
                    else
                    {
                        int group = SkillSlotAllocator.ReformGroup(sd.RawGroup, sd.IsActive, sd.NumGrowtypes);
                        allocatedSlot = SkillSlotAllocator.AllocateNewSlot(sd.IsActive, group, job, occupied);
                        if (allocatedSlot < 0)
                        {
                            result.Success = false;
                            result.ErrorCode = 1;
                            return new BuySkillExecutionPlan { Result = result, Snapshot = snapshot };
                        }
                        slotForEntry = (byte)allocatedSlot;
                    }

                    // 校验4+5: SP/TP 成本按费用表原值分池扣减, 无百分比折扣
                    // ([skill fitness ...] 是从属标记非折扣, 斩铁式+1 成本 45 整实测定案)。
                    if (sd.IsTpSkill)
                    {
                        int tpCost = sd.TpCostFor(curLevel, newLevel);
                        if (!unlimitedPoints)
                        {
                            if (remainTp < tpCost) { result.Success = false; result.ErrorCode = 2; return new BuySkillExecutionPlan { Result = result, Snapshot = snapshot }; }
                            remainTp -= tpCost;
                        }
                    }
                    else
                    {
                        int cost = sd.SpCostFor(curLevel, newLevel);
                        if (!unlimitedPoints)
                        {
                            if (remainSp < cost) { result.Success = false; result.ErrorCode = 2; return new BuySkillExecutionPlan { Result = result, Snapshot = snapshot }; }
                            remainSp -= cost;
                        }
                    }

                    if (existing != null)
                    {
                        existing.Level = (byte)newLevel;
                    }
                    else
                    {
                        occupied.Add(allocatedSlot);
                        page.Entries.Add(new SkillInfoEntrySnapshot
                        {
                            Slot = slotForEntry,
                            SkillId = (ushort)req.SkillIndex,
                            Level = (byte)newLevel,
                        });
                    }

                    result.Entries.Add(CreateResultEntry(
                        (byte)(sd.IsSpecial ? 0xFF : slotForEntry),
                        (ushort)req.SkillIndex,
                        (byte)newLevel,
                        existing));
                }
                else
                {
                    if (existing == null || curLevel == 0) continue;
                    byte refundSlot = existing.Slot;
                    int baseLevel = GetFreeBaselineLevel((byte)job, req.SkillIndex, firstGrow, secondGrow);
                    int newLevel = curLevel - levels;
                    if (newLevel < baseLevel) newLevel = baseLevel;
                    if (newLevel >= curLevel) continue;
                    hasEffectiveRefund = true;
                    if (!sd.IsTpSkill)
                        hasNonTpEffectiveRefund = true;
                    // 退点 100% 返还费用表原值。
                    if (!unlimitedPoints && sd.IsTpSkill)
                    {
                        remainTp += sd.TpCostFor(newLevel, curLevel);
                    }
                    else if (!unlimitedPoints)
                    {
                        remainSp += sd.SpCostFor(newLevel, curLevel);
                    }

                    if (newLevel == 0)
                    {
                        page.Entries.Remove(existing);
                        occupied.Remove(existing.Slot);
                    }
                    else
                    {
                        existing.Level = (byte)newLevel;
                    }

                    result.Entries.Add(CreateResultEntry(
                        (byte)(sd.IsSpecial ? 0xFF : refundSlot),
                        (ushort)req.SkillIndex,
                        (byte)newLevel,
                        existing));
                }
            }

            result.RemainSp = unlimitedPoints ? ushort.MaxValue : ToUInt16(remainSp);
            result.RemainTp = unlimitedPoints ? ushort.MaxValue : ToUInt16(remainTp);
            // 写协议镜像: 保存前将两页 SP/TP 派生值写入 snapshot 的 HeaderValue/Tail,
            // 使 SaveSkillsCore 持久化的镜像值与 Ledger 派生一致。
            if (unlimitedPoints)
            {
                SqlitePvpSkillRepository.ApplyUnlimitedPointMirrors(snapshot);
            }
            else
            {
                var finalPoints = SkillStateService.ResolvePointState(snapshot, (byte)job, level, bonusSp, bonusTp, firstGrow, secondGrow);
                SkillStateService.ApplyProtocolMirrors(snapshot, finalPoints);
            }
            return new BuySkillExecutionPlan
            {
                Result = result,
                Snapshot = snapshot,
                HasEffectiveRefund = hasEffectiveRefund,
                RefundsOnlyTp = hasEffectiveRefund && !hasNonTpEffectiveRefund,
            };
        }

        private static ushort ToUInt16(int value)
        {
            if (value < 0) return 0;
            return value > ushort.MaxValue ? (ushort)ushort.MaxValue : (ushort)value;
        }

        private static BuySkillResultEntry CreateResultEntry(
            byte slot,
            ushort skillId,
            byte level,
            SkillInfoEntrySnapshot existing)
        {
            var entry = new BuySkillResultEntry
            {
                Slot = slot,
                SkillId = skillId,
                Level = level,
            };

            if (level <= 0)
                return entry;

            if (existing?.ExtraValues != null && existing.ExtraValues.Count > 0)
            {
                entry.CommandBytes.AddRange(existing.ExtraValues);
            }
            else
            {
                // A21 BUY_SKILL 成功 ACK 中 TP 技能实测会带默认命令段 01。
                entry.CommandBytes.Add(0x01);
            }

            entry.HasCmd = entry.CommandBytes.Count > 0;
            return entry;
        }

        private static int GetFreeBaselineLevel(byte job, int skillId, int growType, int secondGrowType)
        {
            var baseline = SkillPointLedger.BuildFreeBaseline(job, growType, secondGrowType);
            return baseline.TryGetValue((ushort)skillId, out var lv) ? lv : 0;
        }

        private static void ApplyConsumedRefundItem(
            BuySkillResult result,
            InventoryMainItemConsumeResult consumed,
            int itemTemplateId)
        {
            if (result == null || consumed == null)
                return;

            result.ConsumedForgetRiverWater = true;
            result.ConsumedForgetRiverWaterSlot = consumed.SlotIndex;
            result.ConsumedForgetRiverWaterItem = new InventoryMutationResult
            {
                ListType = InventoryListType.Main,
                SlotIndex = consumed.SlotIndex,
                ItemTemplateId = itemTemplateId,
                RemainingStackCount = consumed.RemainingCount,
                InstanceValue = consumed.RemainingCount,
                RequestedCount = 1,
                AppliedCount = (short)Math.Min(
                    short.MaxValue,
                    consumed.ConsumedCount),
            };
        }

        private static BuySkillResult Failed(int skillTree, byte errorCode)
        {
            return new BuySkillResult
            {
                Success = false,
                SkillTree = (byte)skillTree,
                ErrorCode = errorCode,
            };
        }
    }
}

using DfoServer.Game.CharacterData;
using DfoServer.Game.Inventory;
using DfoServer.Game.KnightShield;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class SelectCharacterPacketBuilder
    {
        // 所有角色统一走静态 init 序列; 旧 packet_sequence 回放表已删除
        public static IEnumerable<byte[]> BuildPacketStream(ISelectCharacterDataSource dataSource, int characterId, int accountId)
            => BuildPacketStream(dataSource, characterId, accountId, NewCharacterInitSequence.Build());

        public static IEnumerable<byte[]> BuildPacketStream(
            ISelectCharacterDataSource dataSource,
            int characterId,
            int accountId,
            SkillInfoSnapshot skillOverride)
            => BuildPacketStream(
                dataSource,
                characterId,
                accountId,
                skillOverride,
                spawnOverride: null);

        public static IEnumerable<byte[]> BuildPacketStream(
            ISelectCharacterDataSource dataSource,
            int characterId,
            int accountId,
            SkillInfoSnapshot skillOverride,
            GameChannelSpawn spawnOverride,
            InitPacketBuilderRegistry registry = null)
            => BuildPacketStream(
                dataSource,
                characterId,
                accountId,
                NewCharacterInitSequence.Build(),
                skillOverride,
                spawnOverride,
                registry);

        internal static IEnumerable<byte[]> BuildPacketStream(
            ISelectCharacterDataSource dataSource,
            int characterId,
            int accountId,
            List<SelectCharacterPacketTemplate> templates,
            SkillInfoSnapshot skillOverride = null,
            GameChannelSpawn spawnOverride = null,
            InitPacketBuilderRegistry registry = null)
        {
            registry ??= new InitPacketBuilderRegistry();
            var snapshot = dataSource.Load(characterId, accountId);

            if (snapshot?.CharacterRecord != null && spawnOverride != null)
                spawnOverride.ApplyTo(snapshot.CharacterRecord);

            if (skillOverride != null)
                snapshot.InitializationSnapshot.SkillInfo = skillOverride;

            if (snapshot.CharacterRecord != null)
                snapshot.InitializationSnapshot.AckCharSlotIndex = snapshot.CharacterRecord.TownId;
            var darkKnightComboSent = false;
            var darkKnightComboTemplateExists = HasTemplate(templates, 0x00, 0x01C0);
            var strikerSupportSent = false;

            foreach (var template in templates)
            {
                if (template.Kind == SelectCharacterPacketTemplateKind.Raw
                    && template.Command == 0x00
                    && template.Type == KnightShieldDeckBodyBuilder.DeckNotificationType)
                {
                    if (KnightShieldDataProvider.IsEligibleCharacter(snapshot.CharacterRecord)
                        && snapshot.KnightShieldDeck != null)
                    {
                        var body = KnightShieldDeckBodyBuilder.BuildDeck(snapshot.KnightShieldDeck);
                        FileLogger.Log(
                            $"[SelectCharacterPacketBuilder] OK cmd=0 type=0x{template.Type:X4} "
                            + $"deck=[{string.Join(",", snapshot.KnightShieldDeck.ShieldItemIds)}]");
                        yield return GamePacketEnvelopeBuilder.Build(0x00, template.Type, body);
                    }
                    continue;
                }

                if (template.Kind == SelectCharacterPacketTemplateKind.Raw
                    && template.Command == 0x00
                    && template.Type == (ushort)NotiPacketTypeA21.TAG_CHARACTER_INFO)
                {
                    if (strikerSupportSent)
                    {
                        FileLogger.Log("[SelectCharacterPacketBuilder] SKIP duplicate cmd=0 type=TAG_CHARACTER_INFO");
                        continue;
                    }

                    // 支援兵动态状态始终替换 occurrence 0，避免保留重复或过期项。
                    // 空 00 00 会清已选；未选支援时不发 0x019F。
                    var strikerSupportBody = BuildStrikerSupportBody(
                        registry,
                        snapshot);
                    strikerSupportSent = true;
                    if (!HasPresentStrikerSupportBody(strikerSupportBody))
                    {
                        FileLogger.Log(
                            "[SelectCharacterPacketBuilder] SKIP empty cmd=0 type=TAG_CHARACTER_INFO");
                        continue;
                    }

                    FileLogger.Log(
                        $"[SelectCharacterPacketBuilder] OK cmd=0 type=0x{(ushort)NotiPacketTypeA21.TAG_CHARACTER_INFO:X4} occ=0 bodyLen={strikerSupportBody.Length}");
                    yield return GamePacketEnvelopeBuilder.Build(
                        0x00,
                        (ushort)NotiPacketTypeA21.TAG_CHARACTER_INFO,
                        strikerSupportBody);
                    continue;
                }

                if (template.Kind == SelectCharacterPacketTemplateKind.ItemList)
                {
                    var body = ItemListPacketBuilder.BuildBody(characterId, accountId, template.ItemListType);
                    yield return GamePacketEnvelopeBuilder.Build(template.Command, template.Type, body);
                    continue;
                }

                bool built;
                byte[] structuredBody;
                if (template.Command == 0x01)
                    built = registry.TryBuildCmd(template.Type, snapshot, out structuredBody);
                else if (template.Command == 0x00)
                    built = registry.TryBuild(template.Type, snapshot, template.OccurrenceIndex, out structuredBody);
                else
                {
                    built = false;
                    structuredBody = null;
                }

                if (built)
                {
                    FileLogger.Log($"[SelectCharacterPacketBuilder] OK cmd={template.Command} type=0x{template.Type:X4}({template.Type}) occ={template.OccurrenceIndex} bodyLen={structuredBody?.Length ?? 0}");
                    yield return GamePacketEnvelopeBuilder.Build(template.Command, template.Type, structuredBody);
                    if (template.Command == 0x00 && template.Type == 0x01C0)
                        darkKnightComboSent = true;
                    if (!darkKnightComboTemplateExists
                        && template.Command == 0x00
                        && template.Type == 0x0013
                        && TryBuildDarkKnightComboSkillInfo(
                            registry,
                            snapshot,
                            out var comboBody))
                    {
                        darkKnightComboSent = true;
                        FileLogger.Log($"[SelectCharacterPacketBuilder] OK cmd=0 type=0x01C0(448) occ=0 bodyLen={comboBody.Length}");
                        yield return GamePacketEnvelopeBuilder.Build(0x00, 0x01C0, comboBody);
                    }
                    continue;
                }

                FileLogger.Log($"[SelectCharacterPacketBuilder] ERROR: no builder for cmd={template.Command} type=0x{template.Type:X4} occ={template.OccurrenceIndex}");
            }

            if (!strikerSupportSent)
            {
                var injectedStrikerSupportBody = BuildStrikerSupportBody(
                    registry,
                    snapshot);
                if (!HasPresentStrikerSupportBody(injectedStrikerSupportBody))
                {
                    FileLogger.Log(
                        "[SelectCharacterPacketBuilder] SKIP empty inject cmd=0 type=TAG_CHARACTER_INFO");
                }
                else
                {
                    FileLogger.Log(
                        $"[SelectCharacterPacketBuilder] INJECT cmd=0 type=0x{(ushort)NotiPacketTypeA21.TAG_CHARACTER_INFO:X4} occ=0 bodyLen={injectedStrikerSupportBody.Length}");
                    yield return GamePacketEnvelopeBuilder.Build(
                        0x00,
                        (ushort)NotiPacketTypeA21.TAG_CHARACTER_INFO,
                        injectedStrikerSupportBody);
                }
            }

            if (!darkKnightComboSent
                && TryBuildDarkKnightComboSkillInfo(
                    registry,
                    snapshot,
                    out var trailingComboBody))
            {
                FileLogger.Log($"[SelectCharacterPacketBuilder] OK cmd=0 type=0x01C0(448) occ=0 bodyLen={trailingComboBody.Length}");
                yield return GamePacketEnvelopeBuilder.Build(0x00, 0x01C0, trailingComboBody);
            }
        }

        private static bool TryBuildDarkKnightComboSkillInfo(
            InitPacketBuilderRegistry registry,
            SelectCharacterDataSnapshot snapshot,
            out byte[] body)
        {
            body = null;
            if (snapshot?.CharacterRecord?.Job != 9)
                return false;

            return registry.TryBuild(0x01C0, snapshot, 0, out body);
        }

        private static byte[] BuildStrikerSupportBody(
            InitPacketBuilderRegistry registry,
            SelectCharacterDataSnapshot snapshot)
        {
            return registry.TryBuild((ushort)NotiPacketTypeA21.TAG_CHARACTER_INFO, snapshot, 0, out var body)
                ? body
                : StrikerSupportTagCharacterBodyBuilder.BuildEmptyBody();
        }

        private static bool HasPresentStrikerSupportBody(byte[] body)
        {
            return body != null && body.Length > 2;
        }

        private static bool HasTemplate(List<SelectCharacterPacketTemplate> templates, byte command, ushort type)
        {
            if (templates == null)
                return false;

            foreach (var template in templates)
            {
                if (template.Command == command && template.Type == type)
                    return true;
            }

            return false;
        }
    }
}

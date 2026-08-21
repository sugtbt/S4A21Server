using DfoServer.Game.Characters;
using DfoServer.Game.Mercenary;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Mercenary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    // 支援兵选择：0x01E5 技能表 ACK；0x01E8 成功只发 NOTI 0x019F，失败才回 2B ACK。
    public sealed class MercenaryHandler
    {
        private static readonly ushort SkillListCommand = (ushort)CmdPacketTypeA21.REQUEST_CHARAC_SKILL_INFO;
        private static readonly ushort SelectSkillCommand = (ushort)CmdPacketTypeA21.SELECT_STRIKER;
        private static readonly ushort TagCharacterInfoNotiType = (ushort)NotiPacketTypeA21.TAG_CHARACTER_INFO;

        private readonly ICharacterRepository _characterRepository;
        private readonly SqliteMercenarySupportRepository _supportRepository;
        private readonly IGameDatabase _database;
        public string ProtocolName => "GameProtocol";

        public MercenaryHandler(
            ICharacterRepository characterRepository,
            IGameDatabase database = null)
        {
            _characterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            _database = database ?? GameDatabase.CreateDefault();
            _supportRepository = new SqliteMercenarySupportRepository(_database);
        }

        public async Task HandleMercenaryRequest(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var accountId = session.Account?.AccountId ?? 0;
            var activeCharacterId = session.Player?.CharacterId ?? 0;
            if (accountId <= 0)
            {
                FileLogger.Log($"[{ProtocolName}] MERCENARY/STRIKER rejected: no authenticated account");
                await SendCommandAck(session, header.type, StrikerSupportSkillListWriter.BuildFailureAck());
                return;
            }

            if (header.type == SelectSkillCommand)
            {
                await HandleSelectSkill(session, body, activeCharacterId, accountId);
                return;
            }

            if (header.type != SkillListCommand)
            {
                FileLogger.Log($"[{ProtocolName}] MERCENARY/STRIKER rejected: unexpected command 0x{header.type:X4}");
                await SendCommandAck(session, header.type, StrikerSupportSkillListWriter.BuildFailureAck());
                return;
            }

            await HandleSkillList(session, body, activeCharacterId, accountId);
        }

        private async Task HandleSkillList(
            EnhancedClientSession session,
            byte[] body,
            int activeCharacterId,
            int accountId)
        {
            if (!MercenaryCommandParser.TryParseSkillInfo(body, out var command))
            {
                FileLogger.Log($"[{ProtocolName}] MERCENARY/STRIKER skill list rejected: invalid body");
                await SendCommandAck(session, SkillListCommand, StrikerSupportSkillListWriter.BuildFailureAck());
                return;
            }

            var roster = ListAccountCharacters(accountId);
            var candidate = StrikerSupportRoster.FindByWireIndex(roster, command.WireSlot);
            if (candidate == null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] MERCENARY/STRIKER skill list rejected: owner={activeCharacterId} " +
                    $"wire={command.WireSlot} echo=0x{command.WireSlotEcho:X4} rosterCount={roster.Count}");
                await SendCommandAck(session, SkillListCommand, StrikerSupportSkillListWriter.BuildFailureAck());
                return;
            }

            IReadOnlyList<StrikerSupportSkillWireEntry> skills;
            try
            {
                skills = StrikerSupportSkillListSource.Load(
                    candidate.CharacterId,
                    candidate.Job,
                    candidate.GrowType,
                    candidate.Level,
                    _database);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] MERCENARY/STRIKER skill list failed cid={candidate.CharacterId}: {ex}");
                await SendCommandAck(session, SkillListCommand, StrikerSupportSkillListWriter.BuildFailureAck());
                return;
            }

            var ack = StrikerSupportSkillListWriter.BuildSkillListSuccessAck(
                command.WireSlotEcho,
                candidate.Job,
                candidate.GrowType,
                skills);
            await SendCommandAck(session, SkillListCommand, ack);
        }

        private async Task HandleSelectSkill(
            EnhancedClientSession session,
            byte[] body,
            int activeCharacterId,
            int accountId)
        {
            if (!MercenaryCommandParser.TryParseSelectStriker(body, out var command))
            {
                FileLogger.Log($"[{ProtocolName}] MERCENARY/STRIKER select rejected: body too short");
                await SendCommandAck(session, SelectSkillCommand, StrikerSupportSkillListWriter.BuildFailureAck());
                return;
            }

            if (activeCharacterId <= 0)
            {
                FileLogger.Log($"[{ProtocolName}] MERCENARY/STRIKER select rejected: no active character");
                await SendCommandAck(session, SelectSkillCommand, StrikerSupportSkillListWriter.BuildFailureAck());
                return;
            }

            var roster = ListAccountCharacters(accountId);
            var candidate = StrikerSupportRoster.FindByWireIndex(roster, command.WireSlot);
            if (StrikerSupportRoster.IsTownClearSelection(candidate, activeCharacterId))
            {
                await HandleClearSelection(session, activeCharacterId, command.WireSlot);
                return;
            }

            if (!StrikerSupportRoster.IsEligibleSupport(candidate, activeCharacterId))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] MERCENARY/STRIKER select rejected: wire={command.WireSlot} " +
                    $"cid={candidate?.CharacterId ?? 0} skill={command.SkillId} is not an eligible support");
                await SendCommandAck(session, SelectSkillCommand, StrikerSupportSkillListWriter.BuildFailureAck());
                return;
            }

            var selectedSkill = FindAvailableSkill(candidate, command.SkillId);
            if (selectedSkill == null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] MERCENARY/STRIKER select rejected: wire={command.WireSlot} " +
                    $"requestedSkill={command.SkillId} is not available from current candidate");
                await SendCommandAck(session, SelectSkillCommand, StrikerSupportSkillListWriter.BuildFailureAck());
                return;
            }

            var state = new MercenarySupportState
            {
                OwnerCharacterId = activeCharacterId,
                Slot = MercenarySupportState.SingletonStateKey,
                SupportCharacterId = candidate.CharacterId,
                SkillId = (ushort)selectedSkill.SkillIndex,
                StrikerSkillId = (ushort)selectedSkill.ComboIndex,
            };

            var tagBody = StrikerSupportTagCharacterPacketBuilder.BuildOwnerMappedBody(
                activeCharacterId,
                state,
                _database);
            if (tagBody == null || tagBody.Length <= 2)
            {
                FileLogger.Log($"[{ProtocolName}] MERCENARY/STRIKER select rejected: dynamic 0x019F build failed");
                await SendCommandAck(session, SelectSkillCommand, StrikerSupportSkillListWriter.BuildFailureAck());
                return;
            }

            try
            {
                _supportRepository.Save(state);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] MERCENARY/STRIKER select persist failed: {ex}");
                await SendCommandAck(session, SelectSkillCommand, StrikerSupportSkillListWriter.BuildFailureAck());
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, TagCharacterInfoNotiType, tagBody));
        }

        private async Task HandleClearSelection(
            EnhancedClientSession session,
            int activeCharacterId,
            byte wireSlot)
        {
            try
            {
                _supportRepository.Clear(activeCharacterId, MercenarySupportState.SingletonStateKey);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] MERCENARY/STRIKER clear persist failed owner={activeCharacterId}: {ex}");
                await SendCommandAck(session, SelectSkillCommand, StrikerSupportSkillListWriter.BuildFailureAck());
                return;
            }

            FileLogger.Log(
                $"[{ProtocolName}] MERCENARY/STRIKER cleared owner={activeCharacterId} wire={wireSlot}");
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                TagCharacterInfoNotiType,
                StrikerSupportTagCharacterBodyBuilder.BuildEmptyBody()));
        }

        private IReadOnlyList<CharacterRecord> ListAccountCharacters(int accountId)
        {
            return _characterRepository.ListByAccount(accountId);
        }

        private static StrikerSkillEntry FindAvailableSkill(
            CharacterRecord candidate,
            ushort requestedSkillId)
        {
            if (candidate == null)
                return null;

            return StrikerSkillDataProvider.GetAvailableSkills(
                    candidate.Job,
                    candidate.GrowType,
                    candidate.Level)
                .FirstOrDefault(skill => skill.SkillIndex == requestedSkillId);
        }

        private static Task SendCommandAck(EnhancedClientSession session, ushort command, byte[] body)
            => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, command, body));
    }
}

using System;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Dungeon;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Handlers.Dungeon
{
    // Compatibility facade: validates the current session/run context, then
    // delegates state transitions and wire projection to their owners.
    internal sealed class AntonNormalConquestNotifier
    {
        private readonly AntonNormalConquestApplicationService _application;
        private readonly AntonNormalConquestNotificationSender _sender;

        internal AntonNormalConquestNotifier(
            SqliteCharacterStateRepository repository)
        {
            _application = new AntonNormalConquestApplicationService(repository);
            _sender = new AntonNormalConquestNotificationSender();
        }

        internal void ConfigureLinkedChallenge(DungeonRun run)
            => _application.ConfigureLinkedChallenge(run);

        // Answer to CMD SEQUENTIAL_DUNGEON_INFO (0x035D): resolve the
        // persisted progress of the sequence the client asked about.
        // Unknown keys and sequences without progress report 0 (not started).
        internal byte ResolveSequentialProgress(int characterId, int configKey)
        {
            if (characterId <= 0)
                return 0;
            return _application.TryRestore(characterId, configKey, out var state)
                ? state.ProgressIndex
                : (byte)0;
        }

        internal async Task RestoreBeforeSelectAsync(
            EnhancedClientSession session)
        {
            if (session?.Player == null || session.Player.CharacterId <= 0)
                return;
            var expectedTownGeneration =
                session.Player.CurrentDungeonRunGeneration;
            if (session.Player.CurrentRun != null)
                return;

            try
            {
                // 只恢复玩家当前所在副本入口区域的序列状态。此前无条件推送
                // 安徒恩序列(key=28/41), 镇魂(area 26)等其他区域的客户端收到
                // 错区域数据后会追问自身区域的序列(CMD 0x035D)。
                if (!Town.TryGetDungeonGateReturnInfo(
                        session.Player.CurTownId,
                        session.Player.CurAreaId,
                        out var gate)
                    || gate.WorldMapAreaId <= 0)
                {
                    return;
                }
                if (!_application.TryRestore(
                        session.Player.CharacterId,
                        gate.WorldMapAreaId,
                        out var state))
                {
                    return;
                }
                await _sender.SendAsync(
                    session,
                    state,
                    "enter-select-dungeon",
                    expectedRun: null,
                    expectedTownGeneration);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[AntonNormal] restore skipped: " +
                    $"cid={session.Player.CharacterId} error={ex.Message}");
            }
        }

        internal async Task ApplyClearAsync(
            EnhancedClientSession session,
            DungeonRun run)
        {
            if (session?.Player == null
                || session.Player.CharacterId <= 0
                || run == null)
            {
                return;
            }

            try
            {
                if (!_application.TryApplyClear(
                        session.Player.CharacterId,
                        run.DungeonId,
                        out var result))
                {
                    return;
                }
                await _sender.SendAsync(
                    session,
                    result.State,
                    "dungeon-clear",
                    run.CaptureIdentity(),
                    expectedTownGeneration: null);
                FileLogger.Log(
                    $"[AntonNormal] clear applied: dungeon={run.DungeonId} " +
                    $"changes={(result.Changes.Count == 0
                        ? "none"
                        : string.Join(",", result.Changes.Select(
                            entry => $"{entry.DungeonId}:{entry.ClearState}")))} " +
                    $"progress={result.State.ProgressIndex}");
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[AntonNormal] clear sync failed and remains retryable: " +
                    $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                    $"error={ex.Message}");
                throw;
            }
        }
    }
}

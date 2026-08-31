using System;
using DfoServer.Game.DailyReset;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Events.RecommendedDungeons
{
    internal sealed class RecommendDungeonClearStatsService
    {
        private readonly RecommendDungeonClearStatsRepository _repository;
        private readonly Func<DateTimeOffset> _nowProvider;

        internal RecommendDungeonClearStatsService(
            IGameDatabase database,
            Func<DateTimeOffset> nowProvider = null)
            : this(
                new RecommendDungeonClearStatsRepository(database),
                nowProvider)
        {
        }

        internal RecommendDungeonClearStatsService(
            RecommendDungeonClearStatsRepository repository,
            Func<DateTimeOffset> nowProvider = null)
        {
            _repository = repository
                ?? throw new ArgumentNullException(nameof(repository));
            _nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
        }

        internal void Initialize()
        {
            _repository.EnsureSchema();
        }

        internal RecommendDungeonClearStatsSnapshot RecordClear(int accountId)
        {
            var now = _nowProvider();
            var utcNow = now.UtcDateTime;
            return _repository.RecordClear(
                accountId,
                DailyResetService.TodayId(utcNow),
                DailyResetService.WeekId(utcNow),
                now.ToUniversalTime().ToUnixTimeSeconds());
        }
    }
}

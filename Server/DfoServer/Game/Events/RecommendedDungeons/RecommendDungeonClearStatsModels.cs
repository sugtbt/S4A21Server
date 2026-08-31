namespace DfoServer.Game.Events.RecommendedDungeons
{
    internal static class RecommendDungeonClearPeriodTypes
    {
        internal const int Day = 1;
        internal const int Week = 2;
    }

    internal sealed class RecommendDungeonClearStatsSnapshot
    {
        public int AccountId { get; set; }

        public int DayId { get; set; }

        public int WeekId { get; set; }

        public int DailyClearCount { get; set; }

        public int WeeklyClearCount { get; set; }
    }
}

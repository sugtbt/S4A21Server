namespace DfoServer.Game.KnightShield
{
    public enum KnightShieldUnlockKind
    {
        Quest = 0,
        Level = 1,
    }

    public sealed class KnightShieldCatalogEntry
    {
        public KnightShieldCatalogEntry(
            int itemId,
            int growType,
            KnightShieldUnlockKind unlockKind,
            int openQuestId,
            int clearQuestId,
            int requiredLevel)
        {
            ItemId = itemId;
            GrowType = growType;
            UnlockKind = unlockKind;
            OpenQuestId = openQuestId;
            ClearQuestId = clearQuestId;
            RequiredLevel = requiredLevel;
        }

        public int ItemId { get; }

        public int GrowType { get; }

        public KnightShieldUnlockKind UnlockKind { get; }

        public int OpenQuestId { get; }

        public int ClearQuestId { get; }

        public int RequiredLevel { get; }
    }
}

using System;

namespace DfoServer.Game.Dungeon
{
    [Flags]
    public enum DungeonMonsterDropSource
    {
        None = 0,
        Gold = 1 << 0,
        GenericItems = 1 << 1,
        MonsterTemplateItems = 1 << 2,
        AreaMaterials = 1 << 3,
        Independent = 1 << 4,
        World = 1 << 5,
        Dimension = 1 << 6,
        All = Gold
            | GenericItems
            | MonsterTemplateItems
            | AreaMaterials
            | Independent
            | World
            | Dimension,
    }

    public enum DungeonDropDefinitionKind
    {
        Standard = 0,
        ImpossibleParty = 1,
        ImpossibleSolo = 2,
    }

    public sealed class DungeonDropPolicy
    {
        public static DungeonDropPolicy Standard { get; } =
            new DungeonDropPolicy(DungeonMonsterDropSource.All);

        // Impossible dungeons use their monster-specific Independent_Drop.etc
        // rules. Gold remains a separate currency roll; generic item catalogs do
        // not participate in the mode's equipment/material reward pool.
        public static DungeonDropPolicy Impossible { get; } =
            new DungeonDropPolicy(
                DungeonMonsterDropSource.Gold
                | DungeonMonsterDropSource.Independent
                | DungeonMonsterDropSource.Dimension);

        private DungeonDropPolicy(DungeonMonsterDropSource allowedSources)
        {
            AllowedSources = allowedSources;
        }

        public DungeonMonsterDropSource AllowedSources { get; }

        public bool Allows(DungeonMonsterDropSource source)
            => source != DungeonMonsterDropSource.None
                && (AllowedSources & source) == source;
    }

    public sealed class DungeonDropDefinition
    {
        internal DungeonDropDefinition(
            int dungeonId,
            int sharedDungeonId,
            int impossibleClassification,
            string sourcePath,
            DungeonDropDefinitionKind kind,
            DungeonDropPolicy policy)
        {
            DungeonId = dungeonId;
            SharedDungeonId = sharedDungeonId;
            ImpossibleClassification = impossibleClassification;
            SourcePath = sourcePath ?? string.Empty;
            Kind = kind;
            Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        }

        public static DungeonDropDefinition Standard { get; } =
            CreateStandard(dungeonId: 0, sourcePath: string.Empty);

        public int DungeonId { get; }
        public int SharedDungeonId { get; }
        public int ImpossibleClassification { get; }
        public string SourcePath { get; }
        public DungeonDropDefinitionKind Kind { get; }
        public DungeonDropPolicy Policy { get; }

        internal static DungeonDropDefinition CreateStandard(
            int dungeonId,
            string sourcePath = "")
            => new DungeonDropDefinition(
                dungeonId,
                sharedDungeonId: -1,
                impossibleClassification: -1,
                sourcePath,
                DungeonDropDefinitionKind.Standard,
                DungeonDropPolicy.Standard);
    }
}

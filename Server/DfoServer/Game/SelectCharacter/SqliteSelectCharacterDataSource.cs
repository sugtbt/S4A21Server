using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeon;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.KnightShield;
using DfoServer.Game.Lottery;
using DfoServer.Game.Settings;
using DfoServer.Game.TitleBook;
using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.SelectCharacter
{
    public sealed class SqliteSelectCharacterDataSource : ISelectCharacterDataSource
    {
        private readonly InventoryCharacterLifecycleService _inventoryLifecycle;
        private readonly SqliteCharacterProgressRepository _initDataRepository;
        private readonly SqliteDarkKnightComboSkillRepository _darkKnightComboSkillRepository;
        private readonly KnightShieldDeckRepository _knightShieldDeckRepository;
        private readonly SqliteUserInfoBlobRepository _userInfoBlobRepository;
        private readonly ICharacterStateRepository _initFlagsRepository;
        private readonly IExpertJobStateRepository _expertJobStateRepository;
        private readonly Quests.QuestNotifySelectionRepository _questNotifySelectionRepository;
        private readonly ICharacterRepository _characterRepository;
        private readonly AccountSettingsRepository _accountSettingsRepository;
        private readonly CharacterTitleBookRepository _titleBookRepository;
        private readonly DailyReset.DailyResetService _dailyResetService;
        private readonly LotteryDoubleRewardPolicy _lotteryDoubleRewardPolicy;
        private readonly TitleBookMutationService _titleBookMutationService;
        private readonly HonorLevelSyncService _honorLevel;
        private readonly CharacterGoldLimitRepository _goldLimitRepository;
        private readonly DungeonDifficultyPermissionService
            _dungeonDifficultyPermissions;
        private readonly Infrastructure.IGameDatabase _database;
        private readonly string _connectionString;
        private readonly Quests.DailyChallengeService _dailyChallengeService;

        public SqliteSelectCharacterDataSource(
            string databasePath,
            string schemaFilePath,
            ICharacterRepository characterRepository,
            InventoryCharacterLifecycleService inventoryLifecycle = null,
            IRentalTimeProvider rentalTimeProvider = null,
            DailyReset.DailyResetService dailyResetService = null)
            : this(
                new Infrastructure.GameDatabase(databasePath, schemaFilePath),
                characterRepository,
                inventoryLifecycle,
                rentalTimeProvider,
                dailyResetService)
        {
        }

        public SqliteSelectCharacterDataSource(
            Infrastructure.IGameDatabase database,
            ICharacterRepository characterRepository,
            InventoryCharacterLifecycleService inventoryLifecycle = null,
            IRentalTimeProvider rentalTimeProvider = null,
            DailyReset.DailyResetService dailyResetService = null)
        {
            if (database == null)
                throw new ArgumentNullException(nameof(database));

            _database = database;
            _connectionString = database.ConnectionString;
            var resolvedRentalTimeProvider = rentalTimeProvider ?? SystemRentalTimeProvider.Instance;
            _dailyResetService = dailyResetService ?? new DailyReset.DailyResetService(database);
            _lotteryDoubleRewardPolicy = new LotteryDoubleRewardPolicy(
                _dailyResetService,
                _connectionString);
            _inventoryLifecycle = inventoryLifecycle ?? new InventoryCharacterLifecycleService(
                database,
                resolvedRentalTimeProvider);
            _initDataRepository = new SqliteCharacterProgressRepository(database);
            _darkKnightComboSkillRepository = new SqliteDarkKnightComboSkillRepository(database);
            _knightShieldDeckRepository = KnightShieldDeckRepository.FromConnectionString(_connectionString);
            _userInfoBlobRepository = new SqliteUserInfoBlobRepository(database);
            _initFlagsRepository = new SqliteCharacterStateRepository(database);
            _expertJobStateRepository = new SqliteExpertJobStateRepository(database);
            _questNotifySelectionRepository = new Quests.QuestNotifySelectionRepository(_connectionString);
            _characterRepository = characterRepository;
            _accountSettingsRepository = new AccountSettingsRepository(database);
            _titleBookRepository = new CharacterTitleBookRepository(_connectionString);
            _titleBookMutationService = new TitleBookMutationService();
            _honorLevel = new HonorLevelSyncService(_characterRepository, database);
            _goldLimitRepository = new CharacterGoldLimitRepository(database);
            _dungeonDifficultyPermissions =
                new DungeonDifficultyPermissionService(database);
            _dailyChallengeService = new Quests.DailyChallengeService(
                _connectionString,
                _dailyResetService);
        }

        public int GetSeedCharacterId()
        {
            int dbSeedId = _userInfoBlobRepository.LoadSeedCharacterId();
            return dbSeedId > 0 ? dbSeedId : 1000;
        }

        public CreatureItemListSnapshot LoadCreatureItemListSnapshot(int characterId)
        {
            if (InventoryContext.TryGetLease(characterId, out var lease))
            {
                lock (lease.SyncRoot)
                    return PetInventoryAccessor.BuildCreatureItemListSnapshot(lease.Inventory);
            }

            return _initDataRepository.LoadCreatures(characterId);
        }

        public List<TitleBookCategorySnapshot> LoadTitleBookSnapshots(int characterId)
        {
            if (InventoryContext.TryGetLease(characterId, out var lease))
            {
                lock (lease.SyncRoot)
                    return lease.Inventory.TitleBook.BuildSnapshots();
            }

            return _titleBookRepository.LoadSnapshots(characterId);
        }

        public TitleBookCategorySnapshot LoadTitleBookSnapshot(int characterId, int category)
        {
            if (InventoryContext.TryGetLease(characterId, out var lease))
            {
                lock (lease.SyncRoot)
                    return lease.Inventory.TitleBook.BuildSnapshot(category);
            }

            return _titleBookRepository.LoadSnapshot(characterId, category);
        }

        internal bool TryPutTitleBook(int characterId, int accountId, InventoryListType sourceList, short sourceSlot, int itemId, int category, int bookIndex, out TitleBookMutationResult result)
        {
            result = _titleBookMutationService.PutTitle(characterId, accountId, sourceList, sourceSlot, itemId, category, bookIndex);
            return result.Success;
        }

        internal bool TryPutTitleBook(
            InventoryLease lease,
            int accountId,
            InventoryListType sourceList,
            short sourceSlot,
            int itemId,
            int category,
            int bookIndex,
            out TitleBookMutationResult result)
        {
            result = _titleBookMutationService.PutTitle(
                lease,
                accountId,
                sourceList,
                sourceSlot,
                itemId,
                category,
                bookIndex);
            return result.Success;
        }

        internal bool TryGetTitleBook(int characterId, int accountId, InventoryListType targetList, short targetSlot, int itemId, int category, int bookIndex, out TitleBookMutationResult result)
        {
            result = _titleBookMutationService.GetTitle(characterId, accountId, targetList, targetSlot, itemId, category, bookIndex);
            return result.Success;
        }

        internal bool TryGetTitleBook(
            InventoryLease lease,
            int accountId,
            InventoryListType targetList,
            short targetSlot,
            int itemId,
            int category,
            int bookIndex,
            out TitleBookMutationResult result)
        {
            result = _titleBookMutationService.GetTitle(
                lease,
                accountId,
                targetList,
                targetSlot,
                itemId,
                category,
                bookIndex);
            return result.Success;
        }

        internal bool TryTriggerAchievement(int characterId, int questId, ushort delta1, ushort delta2, ushort delta3, out AchievementTriggerResult result)
        {
            result = _titleBookMutationService.TriggerAchievement(characterId, questId, delta1, delta2, delta3);
            return result.Success;
        }

        internal bool TryTriggerAchievement(
            InventoryLease lease,
            int questId,
            ushort delta1,
            ushort delta2,
            ushort delta3,
            out AchievementTriggerResult result)
        {
            result = _titleBookMutationService.TriggerAchievement(
                lease,
                questId,
                delta1,
                delta2,
                delta3);
            return result.Success;
        }

        internal IReadOnlyList<AchievementTriggerResult> TriggerUseItemAchievements(
            InventoryLease lease,
            int itemId,
            int consumedCount)
        {
            return _titleBookMutationService.TriggerUseItemAchievements(
                lease,
                itemId,
                consumedCount);
        }

        public SelectCharacterDataSnapshot Load(int characterId, int accountId)
        {
            _inventoryLifecycle.DeleteExpiredNameTagState(characterId);

            var initSnapshot = new SelectCharacterInitializationSnapshot();

            if (_initDataRepository.HasSkills(characterId))
            {
                initSnapshot.SkillInfo = _initDataRepository.LoadSkills(characterId);
            }
            else
            {
                var seedRec = _characterRepository?.GetById(characterId);
                if (seedRec != null)
                {
                    Characters.CharacterStatComputer.DecodeGrowType(seedRec.GrowType, out var seedFirst, out var seedSecond);
                    var seedSkills = Skills.CharacterSkillProfile.BuildSnapshot(seedRec.Job, seedFirst, seedSecond, seedRec.Level);
                    Skills.SkillStateService.ResolveAndPersist(_initDataRepository, characterId, seedSkills, seedRec.Job, seedRec.Level, seedRec.BonusSp, seedRec.BonusTp, seedFirst, seedSecond);
                    initSnapshot.SkillInfo = seedSkills;
                    FileLogger.Log($"[SelectCharacterDataSource] auto-seeded skills for cid={characterId} job={seedRec.Job} gt=0x{seedRec.GrowType:X2} entries={seedSkills.Pages[0].Entries.Count}+{seedSkills.Pages[1].Entries.Count}");
                }
            }
            var comboSkillBodies = _darkKnightComboSkillRepository.LoadPageBodies(characterId);
            foreach (var comboSkillBody in comboSkillBodies)
                initSnapshot.DarkKnightComboSkillInfoBodies.Add(comboSkillBody);
            SanitizeDarkKnightComboSkillInfo(initSnapshot);
            initSnapshot.CreatureItemList = LoadCreatureItemListSnapshot(characterId);

            _dailyChallengeService.EnsureInitialized(characterId);
            _initFlagsRepository.LoadAll(characterId, initSnapshot);
            ApplyOnlineItemStates(characterId, initSnapshot);
            var loginPermissions = _dungeonDifficultyPermissions
                .BuildLoginPermissions(
                    accountId,
                    initSnapshot.DungeonPermissions);
            initSnapshot.DungeonPermissions.Clear();
            initSnapshot.DungeonPermissions.AddRange(loginPermissions);
            initSnapshot.QuestNotifyIds.AddRange(
                _questNotifySelectionRepository.Load(characterId));
            initSnapshot.TitleBookCategories.Clear();
            if (InventoryContext.TryGetLease(characterId, out var lease))
            {
                lock (lease.SyncRoot)
                {
                    initSnapshot.AchievementComplete = lease.Inventory.Achievements.BuildSnapshot();
                    initSnapshot.TitleBookCategories.AddRange(lease.Inventory.TitleBook.BuildSnapshots());
                }
            }
            else
            {
                for (var category = 0; category < TitleBookStaticDataProvider.CategoryCapacities.Count; category++)
                    initSnapshot.TitleBookCategories.Add(
                        _titleBookRepository.LoadSnapshot(characterId, category));
            }

            
            {
                var rec = _characterRepository?.GetById(characterId);
                if (rec != null && initSnapshot.SkillInfo != null && initSnapshot.SkillInfo.Pages.Count > 0)
                {
                    Characters.CharacterStatComputer.DecodeGrowType(rec.GrowType, out var firstGrow, out var secondGrow);
                    var synced = Skills.SkillStateService.LoadAndSync(
                        _initDataRepository,
                        characterId,
                        rec.Job,
                        rec.Level,
                        rec.BonusSp,
                        rec.BonusTp,
                        // 选角是已保存技能进入全部 A21 USERINFO/SKILLINFO 投影前的
                        // 统一同步边界；同步过程中发现历史错误转职技能时立即清理。
                        persist: true,
                        growType: firstGrow,
                        secondGrowType: secondGrow);
                    initSnapshot.SkillInfo = synced.Skills;
                    SanitizeDarkKnightComboSkillInfo(initSnapshot);
                }
            }

            
            LoadInitFieldsFromPacketTemplates(characterId, initSnapshot);
            initSnapshot.UsableCountItems.AddRange(
                UsableCountLimitService.LoadCurrentDayItems(_connectionString, characterId));
            LoadEpicPieceItems(characterId, accountId, initSnapshot);
            ApplyOnlineItemLockList(characterId, initSnapshot);
            // EQUIPMENT_RENTAL_LIST 是可变状态，加载模板后立即用当前背包/装备租赁重建。
            var rebuiltRentalInfo = _inventoryLifecycle.RebuildRentalInfoFromInventory(
                characterId,
                accountId,
                initSnapshot.RentalInfo);
            initSnapshot.RentalInfo.ReplaceItems(rebuiltRentalInfo.Items);

            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                var wallet = CurrencyService.LoadWallet(conn, null, characterId);
                ApplyWallet(initSnapshot, wallet);
            }

            var acctSettings = _accountSettingsRepository.Load(accountId);
            var character = _characterRepository?.GetById(characterId);
            if (character != null)
            {
                var goldLimits = _goldLimitRepository.LoadOrCreate(characterId, character.Level);
                initSnapshot.GoldLimitUpgradeLevel = goldLimits.UpgradeLevel;
            }
            initSnapshot.MainGameOptionBlob = (byte[])(acctSettings?.MainGameOption
                ?? Settings.AccountSettings.DefaultMainGameOption).Clone();
            initSnapshot.QuickchatBank0 = acctSettings?.QuickchatBank0;
            initSnapshot.QuickchatBank1 = acctSettings?.QuickchatBank1;
            var hkSlots = initSnapshot.HotkeyConfigSlots.Count > 0
                ? BuildHotkeyBlob(initSnapshot.HotkeyConfigSlots)
                : Settings.CharacterKeyboardDefaults.BuildHotkeySlots((byte)(character?.Job ?? 0));
            if (character != null
                && Settings.CharacterKeyboardDefaults.IsCreatorMage(character.Job)
                && Settings.CharacterKeyboardDefaults.LooksLikeNormalDefaultHotkeySlots(hkSlots))
            {
                hkSlots = Settings.CharacterKeyboardDefaults.BuildHotkeySlots(character.Job);
                _initFlagsRepository.SaveHotkeyConfig(characterId, hkSlots);
            }
            if (hkSlots != null && hkSlots.Length >= 2)
            {
                initSnapshot.HotkeyKeyType = character != null && Settings.CharacterKeyboardDefaults.IsCreatorMage(character.Job)
                    ? (byte)1
                    : (acctSettings?.HotkeyKeyType ?? 0);
                initSnapshot.HotkeyConfigSlots.Clear();
                for (int i = 0; i + 1 < hkSlots.Length; i += 2)
                    initSnapshot.HotkeyConfigSlots.Add(BitConverter.ToUInt16(hkSlots, i));
            }


            initSnapshot.ShopCoinEventFlag = _dailyResetService.IsClaimed(characterId, ReviveCoin.ReviveCoinService.DailyClaimKey) ? (byte)1 : (byte)0;

            LoadAccountPremiums(accountId, initSnapshot);

            
            
            
            CharacterRecord characterRecord = _characterRepository?.GetById(characterId);
            var knightShieldDeck = KnightShieldDataProvider.IsEligibleCharacter(characterRecord)
                ? _knightShieldDeckRepository.Load(characterId)
                : null;
            if (characterRecord != null)
            {
                // 选角初始化 USERINFO 同样必须使用当前穿戴栏重建外观，避免 characters.appearance_blob在新建角色或换装后滞留为空/旧值，导致城镇模型和选人/副本显示不一致。
                characterRecord.Appearance = Game.Appearance.AppearanceService.LoadOnlineAppearanceFromInventory(
                    characterId,
                    characterRecord.Job,
                    characterRecord.GrowType,
                    knightShieldDeck,
                    _database);
            }

            var accountCharacters = _characterRepository?.ListByAccount(accountId);
            var adventureGroup = AdventureGroupDataProvider.Calculate(accountCharacters);
            var honorLevel = _honorLevel.LoadSummary(accountId, accountCharacters);
            PersistAdventureManageLevel(accountCharacters, adventureGroup.ManageLevel);


            
            var subtype1Repo = new CharacterData.SqliteSubtype1Repository(
                _database);
            if (subtype1Repo.HasData(characterId))
            {
                initSnapshot.UserInfoAddition = subtype1Repo.Load(characterId, knightShieldDeck);
                if (initSnapshot.UserInfoAddition != null)
                {
                    AdventureGroupUserInfoSynchronizer.ApplyToUserInfoAddition(
                        initSnapshot.UserInfoAddition,
                        adventureGroup);
                    HonorLevelDataProvider.ApplyToUserInfoAddition(
                        initSnapshot.UserInfoAddition,
                        honorLevel);
                }
            }

            
            if (characterRecord != null)
            {
                var tailSnap = new CharacterData.SqliteSubtype0FieldsRepository(
                    _database).Load(characterId);
                if (tailSnap != null)
                    characterRecord.Subtype0Tail = tailSnap;

                var expertJobType = characterRecord.Subtype0Tail?.ExpertJobType ?? 0;
                var expertJobState = _expertJobStateRepository.Load(
                    characterId,
                    expertJobType);
                ExpertJobStateCodec.ProjectToSnapshot(
                    expertJobType,
                    expertJobState,
                    initSnapshot.ExpertJobInfo,
                    characterRecord.Subtype0Tail?.ExpertJobExp ?? 0);

                if (characterRecord.Subtype0Tail != null)
                {
                    Settings.AccountSettings.TryApplyCharacterVisibilityBitsToOptions(
                        initSnapshot.MainGameOptionBlob,
                        characterRecord.Subtype0Tail.UserStateBits);
                }

                
                if (characterRecord.Subtype0Tail != null && initSnapshot.UserInfoAddition != null)
                {
                    HonorLevelDataProvider.ApplyToSubtype0Tail(characterRecord.Subtype0Tail, honorLevel);
                    characterRecord.Subtype0Tail.SkillTreeIndex = initSnapshot.UserInfoAddition.SkillTreeIndex;
                }
            }

            return new SelectCharacterDataSnapshot
            {
                InitializationSnapshot = initSnapshot,
                KnightShieldDeck = knightShieldDeck,
                CharacterRecord = characterRecord,
            };
        }

        private void LoadEpicPieceItems(
            int characterId,
            int accountId,
            SelectCharacterInitializationSnapshot initSnapshot)
        {
            if (initSnapshot == null)
                return;

            initSnapshot.EpicPieceItems.Clear();
            if (InventoryContext.TryGetLease(characterId, out var lease))
            {
                lock (lease.SyncRoot)
                    initSnapshot.EpicPieceItems.AddRange(lease.Inventory.EpicPieces.BuildEntries());
                return;
            }

            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                var model = new EpicPieceBookModel();
                model.LoadFromBlob(EpicPieceBookRepository.LoadBlob(conn, null, accountId));
                initSnapshot.EpicPieceItems.AddRange(model.BuildEntries());
            }
        }

        internal void PrepareForSkillSynchronization(int characterId, int accountId)
        {
            Skills.ExpertContractSkillReconciler.ReconcileExpiredContractSkills(
                _initDataRepository,
                characterId,
                accountId);
        }

        private static void ApplyOnlineItemLockList(int characterId, SelectCharacterInitializationSnapshot initSnapshot)
        {
            initSnapshot.ItemLockList = new ItemLockListSnapshot();
            if (!InventoryContext.TryGetLease(characterId, out var lease))
                return;

            lock (lease.SyncRoot)
            {
                foreach (var entry in InventoryLockService.LoadEquipmentItemLocks(lease.Inventory))
                {
                    initSnapshot.ItemLockList.Entries.Add(new ItemLockEntrySnapshot
                    {
                        TypeOrList = (byte)entry.ListType,
                        ItemKeyOrSlot = (ushort)entry.SlotIndex,
                        State = 1,
                    });
                }
            }
        }

        private static void ApplyOnlineItemStates(int characterId, SelectCharacterInitializationSnapshot initSnapshot)
        {
            ApplyOnlineItemStates(
                characterId,
                initSnapshot,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        internal static void ApplyOnlineItemStates(
            int characterId,
            SelectCharacterInitializationSnapshot initSnapshot,
            long now)
        {
            if (initSnapshot == null)
                return;

            if (!InventoryContext.TryGetLease(characterId, out var lease))
            {
                ProjectLoadedItemStateSnapshots(initSnapshot.CooltimeItemStates, now);
                ProjectLoadedItemStateSnapshots(initSnapshot.EffectItemStates, now);
                return;
            }

            var removedExpired = false;
            lock (lease.SyncRoot)
            {
                removedExpired = lease.Inventory.ItemStates.RemoveExpired(now) > 0;
                initSnapshot.CooltimeItemStates.Clear();
                initSnapshot.CooltimeItemStates.AddRange(
                    lease.Inventory.ItemStates.BuildActiveSnapshots(ItemStateKinds.Cooltime, now));
                initSnapshot.EffectItemStates.Clear();
                initSnapshot.EffectItemStates.AddRange(
                    lease.Inventory.ItemStates.BuildActiveSnapshots(ItemStateKinds.Effect, now));
            }

            if (removedExpired)
                InventoryPersistenceService.SaveDirty(lease);
        }

        private static void ProjectLoadedItemStateSnapshots(
            List<ItemStateEntrySnapshot> items,
            long now)
        {
            if (items == null)
                return;

            for (var index = items.Count - 1; index >= 0; index--)
            {
                var item = items[index];
                if (item == null || item.ItemId <= 0)
                {
                    items.RemoveAt(index);
                    continue;
                }

                var remainingSeconds = item.ExpireTime - now;
                if (remainingSeconds <= 0)
                {
                    items.RemoveAt(index);
                    continue;
                }

                item.ExpireTime = remainingSeconds > int.MaxValue
                    ? int.MaxValue
                    : (int)remainingSeconds;
            }
        }

        private static void ApplyWallet(SelectCharacterInitializationSnapshot initSnapshot, WalletSnapshot wallet)
        {
            if (initSnapshot == null || wallet == null)
                return;

            initSnapshot.AckCera = wallet.Cera;
            initSnapshot.AckTokenCera = wallet.TokenCera;
            initSnapshot.AckHappyTokenCera = wallet.HappyTokenCera;
            initSnapshot.LuckyStar = wallet.LuckyStar;
        }

        private static byte[] BuildHotkeyBlob(IReadOnlyList<ushort> slots)
        {
            var count = slots?.Count ?? 0;
            var result = new byte[count * 2];
            for (var i = 0; i < count; i++)
                Buffer.BlockCopy(BitConverter.GetBytes(slots[i]), 0, result, i * 2, 2);
            return result;
        }

        private static void SanitizeDarkKnightComboSkillInfo(SelectCharacterInitializationSnapshot initSnapshot)
        {
            if (initSnapshot?.SkillInfo?.Pages == null || initSnapshot.DarkKnightComboSkillInfoBodies.Count == 0)
                return;

            var rootSkillIds = Skills.DarkKnightComboSkillInfoCodec.GetRootSkillIds(
                initSnapshot.DarkKnightComboSkillInfoBodies);
            if (rootSkillIds.Count == 0)
                return;

            foreach (var page in initSnapshot.SkillInfo.Pages)
            {
                foreach (var entry in page.Entries)
                {
                    if (rootSkillIds.Contains(entry.SkillId))
                        entry.ExtraValues.Clear();
                }
            }
        }

        private void PersistAdventureManageLevel(IReadOnlyList<CharacterRecord> accountCharacters, byte manageLevel)
        {
            if (accountCharacters == null || accountCharacters.Count == 0)
                return;

            try
            {
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    using (var tx = conn.BeginTransaction())
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = @"INSERT INTO character_subtype1_fields(character_id, manage_level)
VALUES (@cid, @level)
ON CONFLICT(character_id) DO UPDATE SET manage_level=excluded.manage_level;";
                        var cidParam = cmd.CreateParameter();
                        cidParam.ParameterName = "@cid";
                        cmd.Parameters.Add(cidParam);
                        var levelParam = cmd.CreateParameter();
                        levelParam.ParameterName = "@level";
                        levelParam.Value = (int)manageLevel;
                        cmd.Parameters.Add(levelParam);

                        foreach (var character in accountCharacters)
                        {
                            if (character == null || character.CharacterId <= 0)
                                continue;
                            cidParam.Value = character.CharacterId;
                            cmd.ExecuteNonQuery();
                        }
                        tx.Commit();
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GameProtocol] Adventure manage_level persist failed: {ex.Message}");
            }
        }

        private void LoadAccountPremiums(int accountId, SelectCharacterInitializationSnapshot initSnapshot)
        {
            initSnapshot.AckPremiums.Clear();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long devilContractMaxExpire = 0;

            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT premium_type, end_time FROM account_premiums WHERE account_id=@aid AND end_time>@now ORDER BY premium_type;";
                    cmd.Parameters.AddWithValue("@aid", accountId);
                    cmd.Parameters.AddWithValue("@now", now);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var pt = reader.GetInt32(0);
                            var endTime = reader.GetInt64(1);

                            if (Premium.DevilContractCatalog.IsDevilContractSlotType(pt))
                            {
                                if (endTime > devilContractMaxExpire)
                                    devilContractMaxExpire = endTime;
                                continue;
                            }

                            var remaining = Math.Max(0, endTime - now);
                            initSnapshot.AckPremiums.Add(new AckPremiumEntrySnapshot
                            {
                                PremiumType = (byte)pt,
                                EndTime = BitConverter.GetBytes(remaining),
                            });
                        }
                    }
                }
            }

            if (devilContractMaxExpire > now)
            {
                initSnapshot.AckPremiums.Add(new AckPremiumEntrySnapshot
                {
                    PremiumType = (byte)Premium.DevilContractCatalog.ActivationPremiumType,
                    EndTime = BitConverter.GetBytes(devilContractMaxExpire - now),
                });
            }
        }
        public bool TrySaveCrystalContractSelection(int characterId, byte[] body)
        {
            if (characterId <= 0 || body == null || body.Length < 2)
                return false;

            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    @"INSERT INTO character_crystal_contract (character_id, cube_type, cube_grade)
                      VALUES (@cid, @t, @g)
                      ON CONFLICT(character_id)
                      DO UPDATE SET cube_type=@t, cube_grade=@g", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@t", (int)body[0]);
                    cmd.Parameters.AddWithValue("@g", (int)body[1]);
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public byte[] LoadAccountMainOption(int accountId)
            => _accountSettingsRepository.Load(accountId)?.MainGameOption;


        public void InitializeNewCharacter(int characterId, int accountId, byte job)
        {
            _inventoryLifecycle.EnsureContainerState(characterId, accountId);

            _initFlagsRepository.InitializeEmptyState(characterId);

            var initialSkills = Skills.CharacterSkillProfile.BuildSnapshot(job, 0, 0, 1);
            if (initialSkills != null)
            {
                var initialDarkKnightComboBodies = job == 9
                    ? DarkKnightInitialSkillLayout.BuildDefaultComboSkillInfoBodies(initialSkills)
                    : null;
                Skills.SkillStateService.ResolveAndPersist(_initDataRepository, characterId, initialSkills, job, 1, 0, 0);
                if (initialDarkKnightComboBodies != null)
                    _darkKnightComboSkillRepository.SavePageBodies(characterId, initialDarkKnightComboBodies);
            }

            var initialEquip = InitialCharacterEquipment.Get(job);
            if (initialEquip != null)
            {
                _inventoryLifecycle.SeedNewCharacterEquipment(characterId, accountId, initialEquip);
            }

            _initFlagsRepository.SaveHotkeyConfig(characterId, Settings.CharacterKeyboardDefaults.BuildHotkeySlots(job));

            SeedNewCharacterStructuredData(characterId, job);
        }

        private void SeedNewCharacterStructuredData(int characterId, byte job)
        {
            using (var conn = _database.OpenConnection())
            {
                
                
                
                var stat = Game.Characters.CharacterStatComputer.BuildAdditionalInfo(job, 1);
                using (var cmd = new SqliteCommand(@"INSERT OR IGNORE INTO character_subtype1_fields(
                    character_id, stat_hp_max, stat_mp_max, stat_physical_attack, stat_physical_defense,
                    stat_magical_attack, stat_magical_defense, stat_fire_resistance, stat_water_resistance,
                    stat_dark_resistance, stat_light_resistance, stat_inventory_limit,
                    stat_hp_regen_speed, stat_mp_regen_speed, stat_move_speed, stat_attack_speed,
                    stat_cast_speed, stat_hit_recovery, stat_jump_power, stat_weight, stat_level,
                    name_tag_item_id, name_tag_expire_time, skill_tree_index, equipped_creature_level, equip_list_trailing,
                    manage_level, flag_byte, guild_power_war, server_timestamp, quest_shop_count,
                    progress1, progress2
                ) VALUES(
                    @cid, @hp, @mp, @pa, @pd, @ma, @md, @fr, @wr, @dr, @lr, @il,
                    @hr, @mr, @ms, @as2, @cs, @hrc, @jp, @wt, 100,
                    0, 0, -1, 0, 0, 0, 0, 0, 0, 0, 0, 0
                )", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    
                    int o = 0;
                    cmd.Parameters.AddWithValue("@hp", (long)System.BitConverter.ToUInt32(stat, o)); o += 4;
                    cmd.Parameters.AddWithValue("@mp", (long)System.BitConverter.ToUInt32(stat, o)); o += 4;
                    cmd.Parameters.AddWithValue("@pa", (int)System.BitConverter.ToInt16(stat, o)); o += 2;
                    cmd.Parameters.AddWithValue("@pd", (int)System.BitConverter.ToInt16(stat, o)); o += 2;
                    cmd.Parameters.AddWithValue("@ma", (int)System.BitConverter.ToInt16(stat, o)); o += 2;
                    cmd.Parameters.AddWithValue("@md", (int)System.BitConverter.ToInt16(stat, o)); o += 2;
                    cmd.Parameters.AddWithValue("@fr", (int)System.BitConverter.ToInt16(stat, o)); o += 2;
                    cmd.Parameters.AddWithValue("@wr", (int)System.BitConverter.ToInt16(stat, o)); o += 2;
                    cmd.Parameters.AddWithValue("@dr", (int)System.BitConverter.ToInt16(stat, o)); o += 2;
                    cmd.Parameters.AddWithValue("@lr", (int)System.BitConverter.ToInt16(stat, o)); o += 2;
                    o += 34; 
                    cmd.Parameters.AddWithValue("@il", (long)System.BitConverter.ToUInt32(stat, o)); o += 4;
                    cmd.Parameters.AddWithValue("@hr", (int)System.BitConverter.ToUInt16(stat, o)); o += 2;
                    cmd.Parameters.AddWithValue("@mr", (int)System.BitConverter.ToUInt16(stat, o)); o += 2;
                    cmd.Parameters.AddWithValue("@ms", (long)System.BitConverter.ToUInt32(stat, o)); o += 4;
                    cmd.Parameters.AddWithValue("@as2", (int)System.BitConverter.ToUInt16(stat, o)); o += 2;
                    cmd.Parameters.AddWithValue("@cs", (int)System.BitConverter.ToUInt16(stat, o)); o += 2;
                    cmd.Parameters.AddWithValue("@hrc", (int)System.BitConverter.ToUInt16(stat, o)); o += 2;
                    cmd.Parameters.AddWithValue("@jp", (int)System.BitConverter.ToUInt16(stat, o)); o += 2;
                    cmd.Parameters.AddWithValue("@wt", (long)System.BitConverter.ToUInt32(stat, o));
                    cmd.ExecuteNonQuery();
                }

                
            }
        }

        private void LoadInitFieldsFromPacketTemplates(int characterId, SelectCharacterInitializationSnapshot snap)
        {
            LoadCrystalContract(characterId, snap);
        }

        private void LoadCrystalContract(int characterId, SelectCharacterInitializationSnapshot snap)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT cube_type, cube_grade FROM character_crystal_contract WHERE character_id=@cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read())
                            return;
                        snap.CubeType = (byte)reader.GetInt32(0);
                        snap.CubeGrade = (byte)reader.GetInt32(1);
                    }
                }
            }
        }
    }
}

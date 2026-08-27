using DfoServer.Network;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer
{
    internal class Program
    {
        // 自测注册表: A21 协议自测; 单跑参数与 --selftest-all 都会覆盖到。
        private static readonly (string Arg, Func<int> Run)[] SelfTestRegistry =
        {
            ("--selftest-a21-startup-protocol", SelfTests.A21StartupProtocolSelfTest.Run),
            ("--selftest-a21-channel-protocol", SelfTests.A21ChannelProtocolSelfTest.Run),
            ("--selftest-a21-create-character-protocol", SelfTests.A21CreateCharacterProtocolSelfTest.Run),
            ("--selftest-a21-tutorial-protocol", SelfTests.A21TutorialProtocolSelfTest.Run),
            ("--selftest-story-book-info-replay", SelfTests.StoryBookInfoReplaySelfTest.Run),
            ("--selftest-a21-adventure-group-protocol", SelfTests.A21AdventureGroupProtocolSelfTest.Run),
            ("--selftest-other-user-info-protocol", SelfTests.OtherUserInfoProtocolSelfTest.Run),
            ("--selftest-a21-mailbox-protocol", SelfTests.A21MailboxProtocolSelfTest.Run),
            ("--selftest-premium-contract-protocol", SelfTests.PremiumContractProtocolSelfTest.Run),
            ("--selftest-a21-guild-medal-guardian-gem", SelfTests.A21GuildMedalGuardianGemSelfTest.Run),
            ("--selftest-a21-equipment-durability", SelfTests.A21EquipmentDurabilitySelfTest.Run),
            ("--selftest-a21-dungeon-drop-item", SelfTests.A21DungeonDropItemSelfTest.Run),
            ("--selftest-enchant-by-bead-listtype", SelfTests.EnchantByBeadListTypeSelfTest.Run),
            ("--selftest-buy-skill-tp-refund", SelfTests.BuySkillTpRefundSelfTest.Run),
            ("--selftest-compound-item-ack", SelfTests.CompoundItemAckSelfTest.Run),
            ("--selftest-daily-reset-account", SelfTests.DailyResetAccountSelfTest.Run),
            ("--selftest-a21-daily-challenge", SelfTests.A21DailyChallengeSelfTest.Run),
            ("--selftest-a21-joust-event", SelfTests.A21JoustEventSelfTest.Run),
            ("--selftest-a21-death-tower-protocol", SelfTests.A21DeathTowerProtocolSelfTest.Run),
            ("--selftest-a21-special-dungeon-protocol", SelfTests.A21SpecialDungeonProtocolSelfTest.Run),
            ("--selftest-dungeon-entry-limit", SelfTests.DungeonEntryLimitServiceSelfTest.Run),
            ("--selftest-item-state", SelfTests.ItemStateSelfTest.Run),
            ("--selftest-titlebook-use-item", SelfTests.TitleBookUseItemAchievementSelfTest.Run),
            ("--selftest-quest-completion-ticket", SelfTests.QuestCompletionTicketSelfTest.Run),
            ("--selftest-growup-change", SelfTests.GrowupChangeSelfTest.Run),
            ("--selftest-cargo-transport-stone", SelfTests.CargoTransportStoneSelfTest.Run),
            ("--selftest-dye-item", SelfTests.DyeItemSelfTest.Run),
            ("--selftest-item-purchase-limit", SelfTests.ItemPurchaseLimitSelfTest.Run),
            ("--selftest-lottery-item", SelfTests.LotteryItemSelfTest.Run),
            ("--selftest-magic-box-protocol", SelfTests.MagicBoxProtocolSelfTest.Run),
            ("--selftest-gold-limit", SelfTests.GoldLimitSelfTest.Run),
            ("--selftest-friends", SelfTests.UnitedFriendSystemSelfTest.Run),
            ("--selftest-pvf-map-monster-parsing", SelfTests.PvfMapMonsterParsingSelfTest.Run),
        };

        // 顺序跑全部自测, 输出汇总表; 任一失败(或抛异常)退出码为 1。
        private static int RunAllSelfTests()
        {
            var failed = new List<string>();
            foreach (var entry in SelfTestRegistry)
            {
                var name = entry.Arg.Substring("--selftest-".Length);
                Console.WriteLine($"===== [{name}] =====");
                int code;
                try
                {
                    code = entry.Run();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{name}] EXCEPTION: {ex.Message}");
                    code = 1;
                }
                if (code != 0)
                    failed.Add(name);
            }

            Console.WriteLine("===== SELFTEST SUMMARY =====");
            Console.WriteLine($"total={SelfTestRegistry.Length} pass={SelfTestRegistry.Length - failed.Count} fail={failed.Count}");
            foreach (var name in failed)
                Console.WriteLine($"FAIL: {name}");
            return failed.Count == 0 ? 0 : 1;
        }

        private static PartyUdpRelay CreatePartyUdpRelay(string scope)
        {
            var isPvp = string.Equals(
                scope,
                "pvp",
                StringComparison.OrdinalIgnoreCase);
            var enabled = isPvp
                ? GameNetworkConfig.PvpUdpRelayEnabled
                : GameNetworkConfig.UdpRelayEnabled;
            var gateName = isPvp ? "DFO_PVP_UDP_RELAY" : "DFO_UDP_RELAY";
            FileLogger.Log(
                $"[PartyUdpRelay scope={scope}] startup gate " +
                $"{gateName}={(enabled ? 1 : 0)}");
            if (!enabled)
                return null;

            if (GameNetworkConfig.ProxyMode)
            {
                FileLogger.Log(
                    $"[PartyUdpRelay scope={scope}] disabled: " +
                    "proxy mode is not supported");
                return null;
            }

            if (!GameNetworkConfig.UdpRelayPublicIpConfigured ||
                !System.Net.IPAddress.TryParse(
                    GameNetworkConfig.UdpRelayPublicIp,
                    out var publicIp) ||
                publicIp.AddressFamily !=
                    System.Net.Sockets.AddressFamily.InterNetwork ||
                System.Net.IPAddress.IsLoopback(publicIp) ||
                publicIp.Equals(System.Net.IPAddress.Any) ||
                publicIp.Equals(System.Net.IPAddress.Broadcast))
            {
                FileLogger.Log(
                    $"[PartyUdpRelay scope={scope}] disabled: set a " +
                    "non-loopback numeric IPv4 address with " +
                    "DFO_UDP_RELAY_PUBLIC_IP");
                return null;
            }

            var portBase = isPvp
                ? GameNetworkConfig.PvpUdpRelayPortBase
                : GameNetworkConfig.UdpRelayPortBase;
            var portCount = isPvp
                ? GameNetworkConfig.PvpUdpRelayPortCount
                : GameNetworkConfig.UdpRelayPortCount;
            return new PartyUdpRelay(
                publicIp.ToString(),
                portBase,
                portCount,
                scope);
        }

        static void Main(string[] args)
        {
            Infrastructure.ClientTextEncoding.EnsureInitialized();
            args ??= Array.Empty<string>();

            if (Array.IndexOf(args, "--selftest-all") >= 0)
            {
                Environment.Exit(RunAllSelfTests());
                return;
            }

            foreach (var entry in SelfTestRegistry)
            {
                if (Array.IndexOf(args, entry.Arg) >= 0)
                {
                    Environment.Exit(entry.Run());
                    return;
                }
            }

            // 数据库迁移命令只处理显式请求，不影响正常启动参数。
            var migrateIndex = Array.IndexOf(args, "--migrate-a21-inventory-db");
            if (migrateIndex >= 0)
            {
                Environment.Exit(RunA21InventoryDatabaseMigration(args, migrateIndex));
                return;
            }
            GameNetworkConfig.Configure(args);
            GameNetworkConfig.ValidateRelayConfiguration();

            // 频道目录驱动监听集合: 每频道一个独立 TCP 端口(10000+频道号),
            // 客户端连哪个端口, CHANNELINFO 就带哪个频道身份。
            var channelInfoPath = Infrastructure.ServerPaths.ChannelInfoFilePath;
            if (File.Exists(channelInfoPath))
            {
                GameNetworkConfig.ConfigureChannelCatalog(
                    ChannelProtocolHandler.ParseScriptChannelIds(
                        File.ReadAllText(channelInfoPath)));
            }

            PacketFileLogger.Initialize();
            if (GameNetworkConfig.PacketCaptureEnabled)
                Console.WriteLine("[PacketCapture] ENABLED – all SEND/RECV packets logged to packet_log.txt");

            try
            {
                _ = GameWorld.GameWorldConfig.PvfArchivePath;
            }
            catch (System.IO.FileNotFoundException)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: Script.pvf not found.");
                Console.WriteLine("Please place Script.pvf in Data/Pvf/Script.pvf, or set the PVF_ARCHIVE_PATH environment variable.");
                Console.ResetColor();
                Environment.Exit(1);
                return;
            }

            Console.Write("Loading Script.pvf... ");
            try
            {
                GameWorld.PvfArchiveAccessor.ReadText("character/character.lst");
                var itemMetadataWarmupTimer = Stopwatch.StartNew();
                Game.Inventory.ItemMetadataResolver.Warmup();
                itemMetadataWarmupTimer.Stop();
                FileLogger.Log(
                    $"[Startup] ITEM_METADATA_WARMUP totalMs={itemMetadataWarmupTimer.Elapsed.TotalMilliseconds:F3}");
                Game.Dungeon.ClearRewardGenerator.WarmUp();
                Game.Dungeon.DimensionDropSystem.WarmUp();
                GameWorld.DungeonExperienceDefinitionCatalog.WarmUp();
                Game.Dungeon.PassiveObjectDropPlanningService.WarmUp();
                Game.Inventory.EquipmentRegenerationCandidateCatalog.Warmup();
                GameWorld.IndependentDropDefinitionCatalog.WarmUp();
                Game.Inventory.ChronicleRefineMaterialResolver.Warmup();
                Game.Mercenary.StrikerSkillDataProvider.Warmup();
                Game.Mercenary.StrikerDefaultAvatarDataProvider.Warmup();
                Console.WriteLine("OK");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("FAILED");
                Console.WriteLine($"Error: Failed to load Script.pvf: {ex.Message}");
                Console.ResetColor();
                Environment.Exit(1);
                return;
            }

            using var runtimeBuilder = Infrastructure.ServerRuntimeBuilder.CreateDefault();
            var database = runtimeBuilder.Database;

            // 启动时一次性按当前等级重算所有角色战斗属性, 修复历史"升级未重算属性"的存量数据。
            // 必须在 PVF 加载后: 属性表来自 Script.pvf。幂等, 重复执行结果一致, 正常时静默, 仅出错时提示。
            try
            {
                new Game.CharacterData.SqliteSubtype1Repository(database)
                    .RecomputeAllCombatStats();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Startup] combat stats recompute skipped: {ex.Message}");
            }

            var server = new MultiStructureTcpServer();
            var sessionDirectory = new Game.Session.SessionDirectory();

            int channelPort = GameNetworkConfig.ProxyMode ? 7002 : 7001;
            var gameChannels = GameNetworkConfig.GetGameChannels();
            var gameListenerPorts = gameChannels
                .Select(channel => channel.ListenerGamePort)
                .Distinct()
                .ToArray();
            var publicGamePorts = gameChannels
                .Select(channel => channel.PublicGamePort)
                .Distinct()
                .ToArray();

            using var udpRelay = CreatePartyUdpRelay("party");
            using var pvpUdpRelay = CreatePartyUdpRelay("pvp");
            using var gameProtocolHandler = runtimeBuilder.BuildGameProtocolHandler(
                sessionDirectory,
                packet => Task.WhenAll(
                    gameListenerPorts.Select(
                        port => server.BroadcastToPortAsync(port, packet))),
                udpRelay,
                pvpUdpRelay);

            var portConfigs = new Dictionary<int, (IProtocolHandler handler, IPacketHeader structure)>
            {
                { channelPort, (new ChannelProtocolHandler(), new ChannelPacketHeader()) }
            };
            Console.WriteLine(
                "A21WireLayout: 14B game receive header; A21 CHANNELINFO/login layout");
            foreach (var channel in gameChannels)
            {
                portConfigs.Add(
                    channel.ListenerGamePort,
                    (gameProtocolHandler, new GamePacketHeader()));
            }

            server.Start(portConfigs);

            Game.Inventory.InventoryPersistenceService.RegisterClock(Infrastructure.ClockService.Instance);
            Infrastructure.ClockService.Instance.Start();

            if (GameNetworkConfig.ProxyMode)
            {
                Console.WriteLine(
                    $"[ProxyMode] Server listening on {channelPort}(channel) / " +
                    $"{string.Join("/", gameListenerPorts)}(game); " +
                    "PvfProxy forwards the public channel/game ports.");
            }

            Console.WriteLine("Multi-structure TCP server started!");
            Console.WriteLine(
                $"Advertised server IP: {GameNetworkConfig.ServerIp} " +
                $"(port 7001 channel, {string.Join("/", publicGamePorts)} game)");
            if (GameNetworkConfig.FreeDuelListenerEnabled)
            {
                Console.WriteLine(
                    $"[FreeDuel] CH.{GameNetworkConfig.FreeDuelChannelIndex} " +
                    $"listener bound on TCP {GameNetworkConfig.FreeDuelGamePort}.");
            }
            var interactiveConsole = Environment.UserInteractive && !Console.IsInputRedirected;
            Console.WriteLine(interactiveConsole
                ? "Press 's' for statistics, 'q' to quit."
                : "Running without interactive console. Stop the service to quit.");

            if (!interactiveConsole)
            {
                var stopped = new ManualResetEventSlim(false);
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    stopped.Set();
                };
                AppDomain.CurrentDomain.ProcessExit += (sender, e) => stopped.Set();
                stopped.Wait();
            }
            else
            {
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);

                    if (key.KeyChar == 's' || key.KeyChar == 'S')
                    {
                        var stats = server.GetStatistics();
                        Console.WriteLine("\n=== Server Statistics ===");
                        Console.WriteLine($"Total Clients: {stats.TotalClients}");
                        foreach (var stat in stats.PortStats)
                        {
                            var config = portConfigs[stat.Key];
                            Console.WriteLine($"Port {stat.Key} ({config.structure.GetType().Name}): {stat.Value} clients");
                        }
                        Console.WriteLine("=========================\n");
                    }
                    else if (key.KeyChar == 'q' || key.KeyChar == 'Q')
                    {
                        break;
                    }
                }
            }

            server.Stop();
            Game.Inventory.InventoryPersistenceService.SaveAllDirty();
            // 服务停止后不再产生常规业务日志，此时完成队列并等待后台写入结束，避免退出时丢失尾部日志。
            FileLogger.Shutdown(TimeSpan.FromSeconds(5));
            Console.WriteLine("Server stopped.");
        }

        private static int RunA21InventoryDatabaseMigration(string[] args, int commandIndex)
        {
            var databasePath = GetOptionValue(args, "--database-path")
                ?? GetFollowingValue(args, commandIndex)
                ?? Infrastructure.ServerPaths.DatabasePath;

            databasePath = Path.GetFullPath(databasePath);
            if (!File.Exists(databasePath))
            {
                Console.Error.WriteLine("[DbMigration] database not found: " + databasePath);
                return 1;
            }

            try
            {
                using (var connection = new SqliteConnection(
                    Infrastructure.SqliteDatabaseBootstrap.BuildConnectionString(databasePath)))
                {
                    connection.Open();
                    var before = SqliteMigrations.ReadVersion(connection);
                    if (SqliteMigrations.HasCurrentBaseline(connection))
                    {
                        SqliteMigrations.Apply(connection);
                        var after = SqliteMigrations.ReadVersion(connection);

                        if (before == after)
                        {
                            Console.WriteLine(
                                $"[DbMigration] no migration needed: {databasePath} schema v{after}");
                        }
                        else
                        {
                            Console.WriteLine(
                                $"[DbMigration] migrated: {databasePath} schema v{before} -> v{after}");
                        }
                    }
                    else if (LegacyInventoryItemCoreMigration.CanApply(connection))
                    {
                        var result = LegacyInventoryItemCoreMigration.Apply(
                            connection,
                            File.ReadAllText(Infrastructure.ServerPaths.SchemaFilePath));
                        Console.WriteLine(
                            $"[DbMigration] legacy inventory normalized: {databasePath} " +
                            $"schema v{result.BeforeVersion} -> v{result.AfterVersion}; " +
                            $"paddedItemCoreRows={result.PaddedItemCoreRows}; " +
                            $"droppedLegacyTables={result.DroppedLegacyTables}");
                    }
                    else
                    {
                        SqliteMigrations.Apply(connection);
                    }
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[DbMigration] failed: " + ex.Message);
                return 1;
            }
        }

        private static string GetFollowingValue(string[] args, int index)
        {
            if (index < 0 || index + 1 >= args.Length)
                return null;

            var value = args[index + 1];
            return value.StartsWith("--", StringComparison.Ordinal) ? null : value;
        }

        private static string GetOptionValue(string[] args, string optionName)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], optionName, StringComparison.Ordinal))
                {
                    var value = args[i + 1];
                    return value.StartsWith("--", StringComparison.Ordinal) ? null : value;
                }
            }

            return null;
        }
    }
}

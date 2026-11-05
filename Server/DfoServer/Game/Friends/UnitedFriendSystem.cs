using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;

namespace DfoServer.Game.Friends
{
    /// <summary>
    /// 好友关系图 + 推送（上下线 / 状态变更 / 列表刷新）。
    ///
    /// 业务语义（详见好友系统服务端设计文档）：
    ///   - 普通好友单向直接添加/删除：a 添加 b 只记录 a→b，无确认弹窗。
    ///     推送方向 = 谁的面板显示谁（IsFriend(观察者, self)）。
    ///   - 绿/灰图标是【场景实体驱动】：USERINFO 注入实体 → 绿，USER_LEAVE 移除实体 → 灰；
    ///     登出通过 USER_LEAVE 清除实体。实体只在同频道推，上下线通知跨频道也推。
    ///   - 持久化：数据库表 united_friend_relations；单边提交成功后更新内存。
    ///   - 静态 owner，lock 保护；在线变更复用角色 transition gates。
    ///
    /// 协议格式与发送顺序见设计文档 §3~§4。
    /// </summary>
    public static partial class UnitedFriendSystem
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, HashSet<string>> Friends =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private static bool _loaded;
        private static UnitedFriendRepository _repository;
        private static readonly ConditionalWeakTable<ISessionDirectory, BlacklistProjection> Blacklists = new();

        internal static void ConfigureBlacklist(ISessionDirectory sessions, BlacklistProjection projection)
        {
            Blacklists.Remove(sessions);
            Blacklists.Add(sessions, projection);
        }

        private static BlacklistProjection GetBlacklist(ISessionDirectory sessions)
            => sessions != null && Blacklists.TryGetValue(sessions, out var projection) ? projection : null;

        private static bool IsBlacklisted(EnhancedClientSession owner, EnhancedClientSession target, ISessionDirectory sessions)
            => GetBlacklist(sessions)?.IsBlocked(owner.Player.CharacterId, target.Player.CharacterId) == true;

        private static UnitedFriendRepository Repository
        {
            get
            {
                if (_repository != null)
                    return _repository;

                _repository = new UnitedFriendRepository(
                    ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                return _repository;
            }
        }

        private static ICharacterRepository _characterRepository;

        /// <summary>
        /// 离线好友列表节点（Lv/Job/GrowType）用角色仓储：按名字查 DB 取真实值，
        /// 查不到（异常/已删）回退默认值。懒构造，同 Repository 的 DB 路径。
        /// </summary>
        private static ICharacterRepository CharacterRepository
        {
            get
            {
                if (_characterRepository != null)
                    return _characterRepository;

                _characterRepository = new SqliteCharacterRepository(
                    ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                return _characterRepository;
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
                return;
            var relations = Repository.LoadAll();
            foreach (var kv in relations)
            {
                if (string.IsNullOrEmpty(kv.Key))
                    continue;
                foreach (var b in kv.Value)
                {
                    if (!string.IsNullOrEmpty(b))
                        AddEdgeLocked(kv.Key, b);
                }
            }
            _loaded = true;
        }

        private static void AddEdgeLocked(string a, string b)
        {
            if (!Friends.TryGetValue(a, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                Friends[a] = set;
            }
            set.Add(b);
        }

        /// <summary>记录单向好友关系并持久化（幂等）：a 添加 b，只记 a→b。</summary>
        public static void RecordFriendship(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return;

            lock (Sync)
            {
                EnsureLoaded();
                var isNew = !IsFriend(a, b);
                if (isNew)
                {
                    Repository.InsertRelation(a, b);
                    AddEdgeLocked(a, b);
                    FileLogger.Log(
                        $"[UnitedFriend] RecordFriendship \"{a}\" -> \"{b}\" "
                        + $"(单向, united_friend_relations)");
                }
            }
        }

        /// <summary>
        /// 移除单向好友关系 a→b（幂等）。返回该关系是否实际存在并删除。
        /// 单向语义：只删 a 的好友列表里的 b，不动 b→a（若有）。
        /// </summary>
        public static bool RemoveFriendship(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return false;

            lock (Sync)
            {
                EnsureLoaded();
                if (!Friends.TryGetValue(a, out var set) || !set.Contains(b))
                    return false;

                Repository.DeleteRelation(a, b);
                set.Remove(b);
                if (set.Count == 0)
                    Friends.Remove(a);

                FileLogger.Log(
                    $"[UnitedFriend] RemoveFriendship \"{a}\" -/-> \"{b}\" "
                    + $"(单向, united_friend_relations)");
                return true;
            }
        }

        /// <summary>某角色的好友名列表（排序，只读）。</summary>
        public static IReadOnlyList<string> GetFriends(string name)
        {
            if (string.IsNullOrEmpty(name))
                return Array.Empty<string>();

            lock (Sync)
            {
                EnsureLoaded();
                if (Friends.TryGetValue(name, out var set))
                    return set
                        .OrderBy(n => n, StringComparer.Ordinal)
                        .ToArray();
                return Array.Empty<string>();
            }
        }

        /// <summary>a 的好友列表是否包含 b（单向）。</summary>
        public static bool IsFriend(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return false;

            lock (Sync)
            {
                EnsureLoaded();
                return Friends.TryGetValue(a, out var set)
                    && set.Contains(b);
            }
        }

        /// <summary>
        /// 登录 hook：给把自己加为好友的在线角色推「进入频道」通知，支持跨频道。
        /// 同频道按各自的单向好友关系推送 USERINFO，更新在线图标。
        ///
        /// 登录者的初始列表由 UnitedServerFriendInfoBodyBuilder 下发。
        /// 此处负责观察者刷新和同频道好友身份投影。
        /// </summary>
        public static async Task NotifyPlayerEnteredGame(
            EnhancedClientSession self,
            ISessionDirectory dir)
        {
            var selfName = GetPlayerName(self);
            if (string.IsNullOrEmpty(selfName))
                return;

            try
            {
                if (GetBlacklist(dir) is { } blacklist)
                {
                    await blacklist.PublishAsync(self);
                }
                var online = GetOnlineSessions(dir, self);

                // 单向推送：谁的面板显示 self（IsFriend(otherName, selfName)）谁收到 self 通知。
                // 上下线通知跨频道也推；USERINFO 场景实体只在同频道推（见设计文档 §4.2）。
                foreach (var s in online)
                {
                    var otherName = GetPlayerName(s);
                    if (string.IsNullOrEmpty(otherName))
                        continue;

                    var sSeesSelf = IsFriend(otherName, selfName);  // s 的面板显示 self
                    var selfSeesS = IsFriend(selfName, otherName);  // self 的面板显示 s

                    // s 的面板显示 self：先通知进入频道，同频道再推 USERINFO 实体。
                    if (sSeesSelf)
                    {
                        bool blacklisted = IsBlacklisted(s, self, dir);
                        var selfEnterBody = BuildChatNoticeBody(
                            ResolveChannel(self), selfName, blacklisted);
                        if (!await s.TrySendPacketAsync(
                            GamePacketEnvelopeBuilder.Build(0x00, (ushort)NotiPacketTypeA21.INOUT_UNITED_SERVER_FRIEND, selfEnterBody),
                            default, CaptureFriendNoticeCheck(s, self, dir, false, blacklisted))) continue;
                        FileLogger.Log(
                            $"[UnitedFriend] {otherName} → 推 0x0112 进入频道 "
                            + $"{selfName} ch={ResolveChannel(self)} "
                            + $"body({selfEnterBody.Length}B): {BitConverter.ToString(selfEnterBody)}");

                        // USERINFO 只在同频道推：跨频道好友不注册场景实体。
                        if (IsSameChannel(self, s))
                        {
                            // 上线通知携带真实频道；列表将同频道好友的频道字段归零，
                            // 隐藏频道文字，在线图标由后续 USERINFO 实体驱动。
                            var sName = GetPlayerName(s);
                            await SendFriendListAsync(s, dir, GetFriends(sName));
                            FileLogger.Log(
                                $"[UnitedFriend] {sName} 同频道 → 0x0112 后补发 "
                                + $"0x0111 列表刷新（同频道在线好友频道归零）");

                            var selfRecord = BuildUserInfoRecord(self.Player);
                            var selfBody = UserInfoSubtype0Builder.BuildNotificationBody(
                                selfRecord);
                            await s.SendPacketAsync(
                                GamePacketEnvelopeBuilder.Build(0x00, (ushort)NotiPacketTypeA21.USERINFO, selfBody));
                            FileLogger.Log(
                                $"[UnitedFriend] {otherName} 同频道 → 推 USERINFO(0x0002) "
                                + $"上线 {selfName} uid=0x{self.Player.UserId:X4} "
                                + $"body({selfBody.Length}B): {BitConverter.ToString(selfBody)}");
                        }
                    }

                    // self 的面板显示 s：同频道推 s 的 USERINFO 实体。
                    if (selfSeesS && IsSameChannel(self, s))
                    {
                        var record = BuildUserInfoRecord(s.Player);
                        var body = UserInfoSubtype0Builder.BuildNotificationBody(record);
                        await self.SendPacketAsync(
                            GamePacketEnvelopeBuilder.Build(0x00, (ushort)NotiPacketTypeA21.USERINFO, body));
                        FileLogger.Log(
                            $"[UnitedFriend] {selfName} 同频道 → 推 USERINFO(0x0002) "
                            + $"上线 {otherName} uid=0x{s.Player.UserId:X4} "
                            + $"body({body.Length}B): {BitConverter.ToString(body)}");
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[UnitedFriend] NotifyPlayerEnteredGame 失败 "
                    + $"({selfName}): {ex}");
            }
        }

        /// <summary>
        /// 登出 hook：给把 self 加为好友的人推「退出频道」通知（跨频道也推），
        /// 并对同频道者推 USER_LEAVE 移除场景实体 → 图标变灰。
        /// </summary>
        public static async Task NotifyPlayerDisconnected(
            EnhancedClientSession self,
            ISessionDirectory dir)
        {
            var selfName = GetPlayerName(self);
            if (string.IsNullOrEmpty(selfName))
                return;

            try
            {
                foreach (var s in dir.GetAllGameSessions())
                {
                    if (s?.Player == null)
                        continue;

                    var otherName = GetPlayerName(s);
                    if (string.IsNullOrEmpty(otherName)
                        || !IsFriend(otherName, selfName))
                        continue;

                    // channel=0 表示退出频道；先发上下线通知，再发 USER_LEAVE。
                    bool blacklisted = IsBlacklisted(s, self, dir);
                    var leaveBody = BuildChatNoticeBody(0, selfName, blacklisted);
                    if (!await s.TrySendPacketAsync(
                        GamePacketEnvelopeBuilder.Build(0x00, (ushort)NotiPacketTypeA21.INOUT_UNITED_SERVER_FRIEND, leaveBody),
                        default, CaptureFriendNoticeCheck(s, self, dir, true, blacklisted))) continue;
                    FileLogger.Log(
                        $"[UnitedFriend] {selfName} 下线 → 推 0x0112 退出频道 "
                        + $"给 {otherName} body({leaveBody.Length}B): "
                        + $"{BitConverter.ToString(leaveBody)}");

                    // USER_LEAVE 只在同频道推（跨频道好友从未注册实体，无需清理）。
                    // PvP owns UID removal: battle actors must survive until
                    // each recipient leaves its native result scene.
                    if (!IsSameChannel(self, s) || GameNetworkConfig.IsPvpListener(self.ListenerPort))
                        continue;

                    var body = TownAreaNotificationBuilder.BuildUserLeave(
                        self.Player.UserId);
                    await s.SendPacketAsync(
                        GamePacketEnvelopeBuilder.Build(0x00, (ushort)NotiPacketTypeA21.USER_LEAVE, body));
                    FileLogger.Log(
                        $"[UnitedFriend] {selfName} 下线 → 推 USER_LEAVE(0x0006) "
                        + $"给 {otherName} uid=0x{self.Player.UserId:X4} "
                        + $"body({body.Length}B): {BitConverter.ToString(body)}");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[UnitedFriend] NotifyPlayerDisconnected 失败 "
                    + $"({selfName}): {ex}");
            }
        }

        /// <summary>
        /// 状态变更 hook：self 的 UserState 变化时，向把 self 加为好友且同频道的在线会话
        /// 推 USERINFO 更新场景实体 → 好友面板图标刷新（进副本→繁忙、回城→在线，见设计文档 §4.2）。
        /// </summary>
        public static async Task NotifyUserStateChanged(
            EnhancedClientSession self,
            ISessionDirectory dir)
        {
            var selfName = GetPlayerName(self);
            if (string.IsNullOrEmpty(selfName))
                return;

            try
            {
                // UserState 已由调用方更新到 PlayerContext.UserState。
                var selfRecord = BuildUserInfoRecord(self.Player);
                var selfBody = UserInfoSubtype0Builder.BuildNotificationBody(selfRecord);

                foreach (var s in dir.GetAllGameSessions())
                {
                    if (s?.Player == null || ReferenceEquals(s, self))
                        continue;

                    var otherName = GetPlayerName(s);
                    if (string.IsNullOrEmpty(otherName)
                        || !IsFriend(otherName, selfName))
                        continue;

                    if (!IsSameChannel(self, s))
                        continue;

                    await s.SendPacketAsync(
                        GamePacketEnvelopeBuilder.Build(0x00, (ushort)NotiPacketTypeA21.USERINFO, selfBody));
                    FileLogger.Log(
                        $"[UnitedFriend] {otherName} 同频道 → 推 USERINFO(0x0002) "
                        + $"状态变更 {selfName} uid=0x{self.Player.UserId:X4} "
                        + $"UserState={self.Player.UserState} "
                        + $"body({selfBody.Length}B): {BitConverter.ToString(selfBody)}");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[UnitedFriend] NotifyUserStateChanged 失败 "
                    + $"({selfName}): {ex}");
            }
        }

        /// <summary>
        /// 等级/职业变更 hook：self 升级或转职时，向把 self 加为好友的在线会话重推好友列表，
        /// 使 self 的 Lv/Job/GrowType 用最新值刷新。等级/职业是列表节点数据，跨频道好友也能看到，
        /// 故不分频道（与 USERINFO 场景实体的同频道门控不同，见设计文档 §4.2）。
        /// </summary>
        public static async Task NotifyFriendListInfoChanged(
            EnhancedClientSession self,
            ISessionDirectory dir)
        {
            var selfName = GetPlayerName(self);
            if (string.IsNullOrEmpty(selfName) || dir == null)
                return;
            await NotifyFriendListInfoChanged(self.Player, dir);
        }

        /// <summary>
        /// PlayerContext 版重载：城镇任务/转职路径只有 PlayerContext 无会话，按名字取刷新。
        /// 语义与会话版一致：self 升级或转职时，向把 self 加为好友的在线会话重推好友列表，
        /// 使 self 的 Lv/Job/GrowType 用最新值刷新（跨频道不分频道，见设计文档 §4.2）。
        /// </summary>
        public static async Task NotifyFriendListInfoChanged(
            PlayerContext self,
            ISessionDirectory dir)
        {
            var selfName = GetPlayerName(self);
            if (string.IsNullOrEmpty(selfName) || dir == null)
                return;

            try
            {
                foreach (var s in dir.GetAllGameSessions())
                {
                    var otherName = GetPlayerName(s);
                    if (string.IsNullOrEmpty(otherName)
                        || string.Equals(otherName, selfName, StringComparison.Ordinal)
                        || !IsFriend(otherName, selfName))
                        continue;

                    // 重推 s 自己的好友列表：SendFriendListAsync 实时取 Lv/Job/GrowType，
                    // self 的 entry 自动用最新值。图标由场景实体驱动，重推不误刷图标。
                    await SendFriendListAsync(s, dir, GetFriends(otherName));
                    FileLogger.Log(
                        $"[UnitedFriend] {selfName} 等级/职业变更 → "
                        + $"重推好友列表给 {otherName}");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[UnitedFriend] NotifyFriendListInfoChanged 失败 "
                    + $"({selfName}): {ex}");
            }
        }

        /// <summary>
        /// 角色删除 hook：清掉 X 在内存图 + 表里所有关系（owner/friend 两方向），
        /// 并向把 X 加为好友的在线会话推 subcmd=2 删节点 + 全量刷新。
        /// 单向语义：只通知面板显示 X 的人（X 的面板已不存在）。关系键是角色名，
        /// 删除后名字不存在，旧键悬空必须清理。
        /// </summary>
        public static async Task HandleCharacterDeletedAsync(
            string name,
            ISessionDirectory dir)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;

            // 受影响 owner 在变更前快照：变更后 IsFriend(owner, name) 恒为 false，不能现算。
            List<string> affected;
            lock (Sync)
            {
                EnsureLoaded();
                affected = Friends
                    .Where(kv => kv.Value.Contains(name))
                    .Select(kv => kv.Key)
                    .ToList();

                Friends.Remove(name);            // owner 方向：X 加的所有好友一并消失。
                foreach (var kv in Friends.ToArray())
                {
                    // friend 方向：所有把 X 加为好友的 owner 的列表移除 X。
                    if (kv.Value.Remove(name) && kv.Value.Count == 0)
                        Friends.Remove(kv.Key);
                }

                try
                {
                    Repository.DeleteAllRelations(name);
                    FileLogger.Log(
                        $"[UnitedFriend] HandleCharacterDeleted \"{name}\" "
                        + $"→ 表已清理({affected.Count} 个好友受影响)");
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[UnitedFriend] HandleCharacterDeleted \"{name}\" 表清理失败: {ex}");
                }
            }

            await NotifyFriendNameRemovedAsync(name, affected, dir);
        }

        /// <summary>
        /// 角色更名 hook：把内存图 + 表中 X 的所有出现换成 Y（owner 关系改键、
        /// friend 关系跟随新名），并向把 X(旧名) 加为好友的在线会话推 subcmd=2
        /// 删旧节点 + 全量刷新。由角色改名业务调用。
        /// </summary>
        public static async Task HandleCharacterRenamedAsync(
            string oldName,
            string newName,
            ISessionDirectory dir,
            bool relationsCommitted = false)
        {
            if (string.IsNullOrWhiteSpace(oldName)
                || string.IsNullOrWhiteSpace(newName)
                || string.Equals(oldName, newName, StringComparison.Ordinal))
                return;

            // 受影响 owner 在变更前快照（同删除，见 HandleCharacterDeletedAsync）。
            List<string> affected;
            lock (Sync)
            {
                EnsureLoaded();
                affected = Friends
                    .Where(kv => kv.Value.Contains(oldName))
                    .Select(kv => kv.Key)
                    .ToList();

                // owner 方向：X 的整条好友边迁到 Y（若 Y 已有好友边则合并）。
                if (Friends.TryGetValue(oldName, out var set))
                {
                    if (Friends.TryGetValue(newName, out var target))
                        target.UnionWith(set);
                    else
                        Friends[newName] = set;
                    Friends.Remove(oldName);
                }
                // friend 方向：所有把 X 加为好友的 owner 的列表里把 X 换成 Y。
                foreach (var kv in Friends.ToArray())
                {
                    if (kv.Value.Remove(oldName))
                        kv.Value.Add(newName);
                }

                try
                {
                    if (!relationsCommitted)
                        Repository.RenameAll(oldName, newName);
                    FileLogger.Log(
                        $"[UnitedFriend] HandleCharacterRenamed "
                        + $"\"{oldName}\" → \"{newName}\" 表已更新");
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[UnitedFriend] HandleCharacterRenamed "
                        + $"\"{oldName}\" → \"{newName}\" 表更新失败: {ex}");
                }
            }

            await NotifyFriendNameRemovedAsync(oldName, affected, dir);
            var blacklist = GetBlacklist(dir);
            if (blacklist != null)
                foreach (var session in dir.GetAllGameSessions())
                    await blacklist.PublishAsync(session);
        }

        /// <summary>
        /// 向受影响 owner 的在线会话推送：subcmd=2 删除 name 节点 + 全量刷新列表。
        /// 删除/更名共用。先显式删除旧名字节点，再用 subcmd=0 重建完整列表。
        /// dir==null（如纯逻辑自测）时跳过通知。
        /// </summary>
        private static async Task NotifyFriendNameRemovedAsync(
            string name,
            List<string> affectedOwners,
            ISessionDirectory dir)
        {
            if (dir == null || affectedOwners == null || affectedOwners.Count == 0)
                return;

            try
            {
                foreach (var s in dir.GetAllGameSessions())
                {
                    if (s?.Player == null)
                        continue;

                    var owner = GetPlayerName(s);
                    if (string.IsNullOrEmpty(owner)
                        || !affectedOwners.Contains(owner, StringComparer.Ordinal))
                        continue;

                    await SendFriendDeletedAsync(s, name, dir);
                    await SendFriendListAsync(s, dir, GetFriends(owner));
                    FileLogger.Log(
                        $"[UnitedFriend] {name} 删除/更名 → "
                        + $"subcmd=2 删节点+全量刷新给 {owner}");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[UnitedFriend] NotifyFriendNameRemovedAsync 失败 "
                    + $"({name}): {ex}");
            }
        }

        /// <summary>
        /// 加好友成功 hook：被加好友(targetName)在线且与 self 同频道 →
        /// 给 self 推其 USERINFO subtype0 场景实体，更新好友在线图标。
        /// 复用登录 hook 方向2 的组包逻辑；被加好友不在线/不同频道则无操作。
        /// </summary>
        public static async Task NotifyFriendAddedAsync(
            EnhancedClientSession self,
            string targetName,
            ISessionDirectory dir,
            CancellationToken cancellationToken = default)
        {
            if (self?.Player == null || string.IsNullOrWhiteSpace(targetName))
                return;

            var isSelfCurrent = CaptureSessionIdentityCheck(self, dir);
            var selfName = GetPlayerName(self);
            try
            {
                foreach (var s in GetOnlineSessions(dir, self))
                {
                    var otherName = GetPlayerName(s);
                    if (string.IsNullOrEmpty(otherName)
                        || !string.Equals(otherName, targetName,
                            StringComparison.Ordinal))
                        continue;
                    if (!IsSameChannel(self, s))
                        continue;

                    var isTargetCurrent = CaptureSessionIdentityCheck(s, dir);
                    var record = BuildUserInfoRecord(s.Player);
                    var body = UserInfoSubtype0Builder.BuildNotificationBody(record);
                    bool sent = await self.TrySendPacketAsync(
                        GamePacketEnvelopeBuilder.Build(0x00, (ushort)NotiPacketTypeA21.USERINFO, body), cancellationToken,
                        () => isSelfCurrent() && isTargetCurrent() && IsSameChannel(self, s)
                            && IsFriend(selfName, targetName));
                    FileLogger.Log(
                        $"[UnitedFriend] {GetPlayerName(self)} 加好友后同频道 → 推 USERINFO(0x0002) "
                        + $"上线 {targetName} uid=0x{record.CharacterId:X4} sent={sent} "
                        + $"body({body.Length}B): {BitConverter.ToString(body)}");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[UnitedFriend] NotifyFriendAddedAsync 失败 "
                    + $"({GetPlayerName(self)} → {targetName}): {ex}");
            }
        }

        /// <summary>
        /// 构造 USERINFO subtype0 用的 CharacterRecord。
        /// CharacterId/onlineUserId 用真实 Player.UserId（与 USER_LEAVE 一致，
        /// 客户端靠同一 id 把"实体注入"与"实体移除"对应起来）。
        /// </summary>
        internal static CharacterRecord BuildUserInfoRecord(PlayerContext p)
        {
            return new CharacterRecord
            {
                CharacterId = p.UserId,
                Name = p.Name ?? Array.Empty<byte>(),
                Job = p.Job,
                GrowType = p.GrowType,
                Level = p.Level,
                PvpGrade = 0,
                PvpRatingGrade = 0,
                UserState = p.UserState,
                Appearance = p.AppearanceEntries
                    ?? Array.Empty<CharacterAppearanceEntry>(),
                Subtype0Tail = p.Subtype0Tail,
            };
        }

        private static List<EnhancedClientSession> GetOnlineSessions(
            ISessionDirectory dir,
            EnhancedClientSession self)
        {
            if (dir == null)
                return new List<EnhancedClientSession>();
            return dir.GetAllGameSessions()
                .Where(s => !ReferenceEquals(s, self) && s?.Player != null)
                .ToList();
        }

        // Capture before waiting for the send lock: a reused socket is not a reused character identity.
        internal static Func<bool> CaptureSessionIdentityCheck(EnhancedClientSession session, ISessionDirectory dir)
        {
            var characterId = session.Player.CharacterId;
            var userId = session.Player.UserId;
            var name = GetPlayerName(session);
            return () => characterId > 0 && userId != 0
                && session.Player.CharacterId == characterId && session.Player.UserId == userId
                && GetPlayerName(session) == name
                && (dir == null || (dir.TryGet(characterId, out var current) && ReferenceEquals(current, session)));
        }

        private static Func<bool> CaptureFriendNoticeCheck(EnhancedClientSession owner, EnhancedClientSession target,
            ISessionDirectory dir, bool leaving, bool blacklisted)
        {
            var ownerCurrent = CaptureSessionIdentityCheck(owner, dir);
            var targetCurrent = CaptureSessionIdentityCheck(target, dir);
            int id = target.Player.CharacterId;
            ushort uid = target.Player.UserId;
            string name = GetPlayerName(target), ownerName = GetPlayerName(owner);
            return () => ownerCurrent() && target.Player.CharacterId == id && target.Player.UserId == uid
                && GetPlayerName(target) == name && IsFriend(ownerName, name)
                && (leaving ? !dir.TryGet(id, out var registered) || ReferenceEquals(registered, target) : targetCurrent())
                && IsBlacklisted(owner, target, dir) == blacklisted;
        }

        /// <summary>
        /// subcmd=0 一次重建完整列表并刷新面板，空列表也必须发送以清除陈旧节点。
        /// 列表 body 由 BuildFriendListBody 实时组包（在线频道三态 + 离线 DB 数据）。
        /// </summary>
        public static async Task SendFriendListAsync(
            EnhancedClientSession self,
            ISessionDirectory dir,
            IEnumerable<string> friendNames,
            CancellationToken cancellationToken = default)
        {
            if (self?.Player == null)
                return;

            var friends = (friendNames ?? Array.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            var isSelfCurrent = CaptureSessionIdentityCheck(self, dir);
            var selfName = GetPlayerName(self);
            var body = BuildFriendListBody(self, dir, friends);
            // Recheck under the send lock; an old queued snapshot cannot resurrect
            // deleted relations or replace the next selected character's list.
            bool sent = await self.TrySendPacketAsync(
                GamePacketEnvelopeBuilder.Build(0x00, (ushort)NotiPacketTypeA21.UNITED_SERVER_FRIEND_INFO, body),
                cancellationToken, () => isSelfCurrent()
                    && GetFriends(selfName).SequenceEqual(friends, StringComparer.Ordinal));
            // The native reset keeps temporary entries only; restore permanent entries afterwards.
            if (sent && isSelfCurrent() && GetBlacklist(dir) is { } blacklist)
                await blacklist.PublishAsync(self);
            FileLogger.Log(
                $"[UnitedFriend] {GetPlayerName(self)} 好友列表 subcmd=0 "
                + $"count={friends.Count} sent={sent} "
                + $"body({body.Length}B): {BitConverter.ToString(body)}");
        }

        /// <summary>
        /// 组 UNITED_SERVER_FRIEND_INFO subcmd=0 列表 body（字段布局见设计文档 §4.1）。选角 init builder
        /// 与运行时 SendFriendListAsync 共用，保证两条路径组包一致。
        ///
        /// 在线好友按名字查会话（取真实 Lv/Job/GrowType/频道）；离线好友按名字查库取
        /// 真实 Lv/Job/GrowType（一次查询），查不到（异常/已删）才回退默认值。
        /// 频道三态：同频道在线=0（不显示频道文字）, 异频道在线=真实频道, 离线=0。
        /// </summary>
        public static byte[] BuildFriendListBody(
            EnhancedClientSession self,
            ISessionDirectory dir,
            IEnumerable<string> friendNames)
        {
            var friends = (friendNames ?? Array.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

            var online = GetOnlineSessions(dir, self);
            var onlineByName = new Dictionary<string, EnhancedClientSession>(
                StringComparer.Ordinal);
            foreach (var s in online)
            {
                var n = GetPlayerName(s);
                if (!string.IsNullOrEmpty(n))
                    onlineByName[n] = s;
            }

            var offlineRecords = new Dictionary<string, CharacterRecord>(
                StringComparer.Ordinal);
            foreach (var name in friends)
            {
                if (onlineByName.ContainsKey(name))
                    continue;
                try
                {
                    var rec = CharacterRepository.GetByName(name);
                    if (rec != null)
                        offlineRecords[name] = rec;
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[UnitedFriend] 离线好友 {name} 查库失败: {ex.Message}");
                }
            }

            var w = new GamePacketWriter();
            w.WriteInt32(0);                       // subcmd=0
            w.WriteUInt32((uint)friends.Count);

            foreach (var name in friends)
            {
                var nameBytes = ClientTextEncoding.GetBytes(name);
                w.WriteByte(1);                    // 大区 sR=1
                w.WriteUInt16(onlineByName.TryGetValue(name, out var os)
                    ? (IsSameChannel(self, os) ? (ushort)0 : ResolveChannel(os))
                    : (ushort)0);
                // 频道：同频道在线=0（不显示频道文字）, 异频道在线=真实频道, 离线=0
                w.WriteUInt32((uint)nameBytes.Length);
                w.WriteBytes(nameBytes);           // name

                if (onlineByName.TryGetValue(name, out var os2))
                {
                    w.WriteByte(os2.Player.Level);
                    w.WriteUInt32(os2.Player.Job);
                    w.WriteByte(os2.Player.GrowType);
                }
                else if (offlineRecords.TryGetValue(name, out var rec))
                {
                    // 离线好友的等级、职业和转职取自角色仓储。
                    w.WriteByte(rec.Level);
                    w.WriteUInt32(rec.Job);
                    w.WriteByte(rec.GrowType);
                }
                else
                {
                    w.WriteByte(1);                // Lv=1 兜底
                    w.WriteUInt32(0);              // Job=0 鬼剑士 兜底
                    w.WriteByte(0);                // GrowType=0 兜底
                }

                w.WriteByte(0);                    // 黑名单位，普通好友为 0
                w.WriteInt32(0);                   // Z=0
            }
            return w.ToArray();
        }

        /// <summary>
        /// 选角 init builder 用：组 self 的完整好友列表 body（BuildFriendListBody 的
        /// selfName 封装）。self 未水合/无名 → 8 字节空态（[subcmd=0][count=0]）兜底。
        /// </summary>
        public static byte[] BuildFriendListInitBody(
            EnhancedClientSession self,
            ISessionDirectory dir)
        {
            var selfName = GetPlayerName(self);
            if (string.IsNullOrEmpty(selfName))
                return new byte[8];

            return BuildFriendListBody(self, dir, GetFriends(selfName));
        }

        /// <summary>
        /// 给 self 下发好友删除（subcmd=2 单条删节点，字段布局见设计文档 §4.1）。
        /// 删除通知同时清除实体好友标记；subcmd=0 只重建列表，不能替代。
        /// 随后由调用方按剩余好友 SendFriendListAsync 全量刷新。
        /// </summary>
        public static async Task SendFriendDeletedAsync(
            EnhancedClientSession self,
            string deletedName,
            ISessionDirectory dir = null,
            CancellationToken cancellationToken = default)
        {
            if (self?.Player == null || string.IsNullOrWhiteSpace(deletedName))
                return;

            var isSelfCurrent = CaptureSessionIdentityCheck(self, dir);
            var selfName = GetPlayerName(self);
            var nameBytes = ClientTextEncoding.GetBytes(deletedName);
            var w = new GamePacketWriter();
            w.WriteInt32(2);                    // subcmd=2 删除
            w.WriteUInt32(1);                   // count=1
            w.WriteByte(1);                     // 大区 sR=1（与列表 entry 一致）
            w.WriteUInt32((uint)nameBytes.Length);
            w.WriteBytes(nameBytes);            // name

            var body = w.ToArray();
            bool sent = await self.TrySendPacketAsync(
                GamePacketEnvelopeBuilder.Build(0x00, (ushort)NotiPacketTypeA21.UNITED_SERVER_FRIEND_INFO, body), cancellationToken,
                () => isSelfCurrent() && !IsFriend(selfName, deletedName));
            FileLogger.Log(
                $"[UnitedFriend] {GetPlayerName(self)} 好友删除 subcmd=2 "
                + $"name=\"{deletedName}\" sent={sent} body({body.Length}B): {BitConverter.ToString(body)}");
        }

        private static ushort ResolveChannel(EnhancedClientSession s)
        {
            try
            {
                var ch = GameNetworkConfig.ResolveGameChannel(s.ListenerPort);
                if (ch != null && ch.ChannelId > 0)
                    return (ushort)ch.ChannelId;
            }
            catch
            {
            }
            return 0;
        }

        /// <summary>
        /// a 与 b 是否在同一频道（场景实体只在同一频道内可见）。
        /// 频道由监听端口映射得到，见 ResolveChannel。
        /// </summary>
        private static bool IsSameChannel(
            EnhancedClientSession a,
            EnhancedClientSession b)
        {
            if (a?.Player == null || b?.Player == null)
                return false;

            var ca = ResolveChannel(a);
            var cb = ResolveChannel(b);
            return ca > 0 && ca == cb;
        }

        /// <summary>
        /// 构造上下线聊天通知 body（字段布局见设计文档 §4.2）：
        /// channel≠0 → "X 进入频道"；channel==0 → "X 退出频道"。
        /// 黑名单位取接收者当前永久关系，避免上下线通知清除客户端标记。
        /// </summary>
        private static byte[] BuildChatNoticeBody(
            ushort channel,
            string name, bool blacklisted = false)
        {
            var w = new GamePacketWriter();
            w.WriteUInt16(channel);              // 频道：0=退出频道, 真实频道=进入频道
            w.WriteByte(1);                      // 当前服务器标识
            var nameBytes = ClientTextEncoding.GetBytes(name);
            w.WriteUInt32((uint)nameBytes.Length);
            w.WriteBytes(nameBytes);             // name
            w.WriteByte(blacklisted ? (byte)1 : (byte)0);
            return w.ToArray();
        }

        private static string GetPlayerName(PlayerContext p)
        {
            if (p?.Name == null)
                return null;

            var name = ClientTextEncoding.GetString(p.Name);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        private static string GetPlayerName(EnhancedClientSession s)
        {
            return GetPlayerName(s?.Player);
        }

    }
}

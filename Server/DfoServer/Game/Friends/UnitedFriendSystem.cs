using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
    /// 业务语义（详见设计文档 §1.2）：
    ///   - 好友关系【单向】：A 加 B 只记 A→B。推送方向 = 谁的面板显示谁（IsFriend(观察者, self)）。
    ///   - 绿/灰图标是【场景实体驱动】：USERINFO 注入实体 → 绿，USER_LEAVE 移除实体 → 灰；
    ///     实体不会自动消失，登出必须主动 USER_LEAVE。实体只在同频道推，0x0112 聊天跨频道也推。
    ///   - 持久化：数据库表 united_friend_relations（见设计文档 §2.2），写边/删边同步落表。
    ///   - 无 DI 静态单例，lock 保护；表 CRUD 单语句原子。
    ///
    /// 协议/反编译细节见设计文档 §3~§7（本节不重复）。
    /// </summary>
    public static class UnitedFriendSystem
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, HashSet<string>> Friends =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private static bool _loaded;
        private static UnitedFriendRepository _repository;

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
            _loaded = true;

            try
            {
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
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[UnitedFriend] 好友表加载失败: {ex}");
            }
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
                AddEdgeLocked(a, b);
                if (isNew)
                {
                    Repository.InsertRelation(a, b);
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

                set.Remove(b);
                if (set.Count == 0)
                    Friends.Remove(a);

                Repository.DeleteRelation(a, b);
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

        /// <summary>a 与 b 是否为好友（方向无关）。</summary>
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
        /// 登录 hook：给在线好友推 0x0112「进入频道」（跨频道也推），
        /// 并对同频道好友双向互推 USERINFO → 双方图标变绿。
        ///
        /// 初始好友列表 0x0111 由选角 init builder（UnitedServerFriendInfoBodyBuilder）下发，
        /// 本 hook 不再重复下发；此处只处理需要场景/目录上下文的推送。
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
                var online = GetOnlineSessions(dir, self);

                // 单向推送：谁的面板显示 self（IsFriend(otherName, selfName)）谁收到 self 通知。
                // 0x0112 聊天跨频道也推；USERINFO 场景实体只在同频道推（见设计文档 §4.4）。
                foreach (var s in online)
                {
                    var otherName = GetPlayerName(s);
                    if (string.IsNullOrEmpty(otherName))
                        continue;

                    var sSeesSelf = IsFriend(otherName, selfName);  // s 的面板显示 self
                    var selfSeesS = IsFriend(selfName, otherName);  // self 的面板显示 s

                    // 方向1：s 的面板显示 self → 推 0x0112 进入频道 + 同频道再推 USERINFO 实体。
                    if (sSeesSelf)
                    {
                        var selfEnterBody = BuildChatNoticeBody(
                            ResolveChannel(self), selfName);
                        await s.SendPacketAsync(
                            GamePacketEnvelopeBuilder.Build(0x00, 0x0112, selfEnterBody));
                        FileLogger.Log(
                            $"[UnitedFriend] {otherName} → 推 0x0112 进入频道 "
                            + $"{selfName} ch={ResolveChannel(self)} "
                            + $"body({selfEnterBody.Length}B): {BitConverter.ToString(selfEnterBody)}");

                        // USERINFO 只在同频道推：跨频道好友不注册场景实体。
                        if (IsSameChannel(self, s))
                        {
                            // 0x0112 进入频道已把 node+0x54 置为真实频道，故补发
                            // 0x0111 列表刷新把它归零（SendFriendListAsync 对同频道
                            // 在线好友 channel 写 0）→ 同频道在线好友不显示频道文字；
                            // 绿图标由下方 USERINFO 场景实体独立驱动，不受影响。
                            var sName = GetPlayerName(s);
                            await SendFriendListAsync(s, dir, GetFriends(sName));
                            FileLogger.Log(
                                $"[UnitedFriend] {sName} 同频道 → 0x0112 后补发 "
                                + $"0x0111 列表刷新（同频道在线好友频道归零）");

                            var selfRecord = BuildUserInfoRecord(self.Player);
                            var selfBody = UserInfoSubtype0Builder.BuildNotificationBody(
                                selfRecord);
                            await s.SendPacketAsync(
                                GamePacketEnvelopeBuilder.Build(0x00, 0x0002, selfBody));
                            FileLogger.Log(
                                $"[UnitedFriend] {otherName} 同频道 → 推 USERINFO(0x0002) "
                                + $"上线 {selfName} uid=0x{self.Player.UserId:X4} "
                                + $"body({selfBody.Length}B): {BitConverter.ToString(selfBody)}");
                        }
                    }

                    // 方向2：self 的面板显示 s → 同频道推 s 的 USERINFO 实体。
                    if (selfSeesS && IsSameChannel(self, s))
                    {
                        var record = BuildUserInfoRecord(s.Player);
                        var body = UserInfoSubtype0Builder.BuildNotificationBody(record);
                        await self.SendPacketAsync(
                            GamePacketEnvelopeBuilder.Build(0x00, 0x0002, body));
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
        /// 登出 hook：给把 self 加为好友的人推 0x0112「退出频道」（跨频道也推），
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

                    // 0x0112 退出频道：channel=0 → "{selfName} 退出频道"（跨频道也推）。
                    // 顺序：先 0x0112 聊天、后 0x0006 USER_LEAVE。
                    var leaveBody = BuildChatNoticeBody(0, selfName);
                    await s.SendPacketAsync(
                        GamePacketEnvelopeBuilder.Build(0x00, 0x0112, leaveBody));
                    FileLogger.Log(
                        $"[UnitedFriend] {selfName} 下线 → 推 0x0112 退出频道 "
                        + $"给 {otherName} body({leaveBody.Length}B): "
                        + $"{BitConverter.ToString(leaveBody)}");

                    // USER_LEAVE 只在同频道推（跨频道好友从未注册实体，无需清理）。
                    if (!IsSameChannel(self, s))
                        continue;

                    var body = TownAreaNotificationBuilder.BuildUserLeave(
                        self.Player.UserId);
                    await s.SendPacketAsync(
                        GamePacketEnvelopeBuilder.Build(0x00, 0x0006, body));
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
        /// 推 USERINFO 更新场景实体 → 好友面板图标刷新（进副本→繁忙、回城→在线，见设计文档 §4.6）。
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
                        GamePacketEnvelopeBuilder.Build(0x00, 0x0002, selfBody));
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
        /// 故不分频道（与 USERINFO 场景实体的同频道门控不同，见设计文档 §4.7）。
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
        /// 使 self 的 Lv/Job/GrowType 用最新值刷新（跨频道不分频道，见设计文档 §4.7）。
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
        /// 删旧节点 + 全量刷新。预留能力：当前服务端未实现改名卡（见设计文档），
        /// 未来改名入口接入时调用。
        /// </summary>
        public static async Task HandleCharacterRenamedAsync(
            string oldName,
            string newName,
            ISessionDirectory dir)
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
        }

        /// <summary>
        /// 向受影响 owner 的在线会话推送：subcmd=2 删除 name 节点 + 全量刷新列表。
        /// 删除/更名共用。列表节点以名字为键，subcmd=0 全量刷新不清陈旧节点，
        /// 必须先 subcmd=2 删旧节点再全量（见 SendFriendDeletedAsync 注释）。
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

                    await SendFriendDeletedAsync(s, name);
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
        /// 给 self 推其 USERINFO(0x0002 subtype0) 场景实体（好友面板图标变绿）。
        /// 复用登录 hook 方向2 的组包逻辑；被加好友不在线/不同频道则无操作。
        /// </summary>
        public static async Task NotifyFriendAddedAsync(
            EnhancedClientSession self,
            string targetName,
            ISessionDirectory dir)
        {
            if (self?.Player == null || string.IsNullOrWhiteSpace(targetName))
                return;

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

                    var record = BuildUserInfoRecord(s.Player);
                    var body = UserInfoSubtype0Builder.BuildNotificationBody(record);
                    await self.SendPacketAsync(
                        GamePacketEnvelopeBuilder.Build(0x00, 0x0002, body));
                    FileLogger.Log(
                        $"[UnitedFriend] {GetPlayerName(self)} 加好友后同频道 → 推 USERINFO(0x0002) "
                        + $"上线 {targetName} uid=0x{s.Player.UserId:X4} "
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
        /// 构造 0x0002 subtype0 用的 CharacterRecord。
        /// CharacterId/onlineUserId 用真实 Player.UserId（与 USER_LEAVE 一致，
        /// 客户端靠同一 id 把"实体注入"与"实体移除"对应起来）。
        /// </summary>
        private static CharacterRecord BuildUserInfoRecord(PlayerContext p)
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

        /// <summary>
        /// 给 self 下发好友列表（0x0111 subcmd=0 连发两遍：第1遍建节点 +0x5D=0 被 UI 过滤，
        /// 第2遍命中 0x14E0730 路径1 置 node+0x5D=1 使其显示）。登录 hook 与加好友成功路径复用。
        /// 列表 body 由 BuildFriendListBody 实时组包（在线频道三态 + 离线 DB 数据）。
        /// </summary>
        public static async Task SendFriendListAsync(
            EnhancedClientSession self,
            ISessionDirectory dir,
            IEnumerable<string> friendNames)
        {
            if (self?.Player == null)
                return;

            var friends = (friendNames ?? Array.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
            if (friends.Count == 0)
                return;

            // 连发两遍：第1遍建节点（+0x5D=0 被 UI 过滤隐藏），
            // 第2遍命中 0x14E0730 路径1（节点已存在）置 +0x5D=1 显示。
            for (int i = 0; i < 2; i++)
            {
                var body = BuildFriendListBody(self, dir, friends);
                await self.SendPacketAsync(
                    GamePacketEnvelopeBuilder.Build(0x00, 0x0111, body));
                FileLogger.Log(
                    $"[UnitedFriend] {GetPlayerName(self)} 好友列表 subcmd=0 "
                    + $"第{i + 1}遍 count={friends.Count} "
                    + $"body({body.Length}B): {BitConverter.ToString(body)}");
            }
        }

        /// <summary>
        /// 组 0x0111 subcmd=0 列表 body（字段布局见设计文档 §4.3）。选角 init builder
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
                    // 离线好友：用 DB 真实记录（Lv/Job/GrowType），不再固定默认值。
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

                w.WriteByte(0);                    // Y=0（全链路无消费方）
                w.WriteInt32(0);                   // Z=0
            }
            return w.ToArray();
        }

        /// <summary>
        /// 选角 init builder 用：组 self 的 0x0111 列表 body（BuildFriendListBody 的
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
        /// 给 self 下发好友删除（0x0111 subcmd=2 单条删节点，字段布局见设计文档 §4.3）。
        /// 删除必须走 subcmd=2：subcmd=0 全量刷新不清陈旧节点，只刷 subcmd=0 被删节点会残留。
        /// 随后由调用方按剩余好友 SendFriendListAsync 全量刷新。
        /// </summary>
        public static async Task SendFriendDeletedAsync(
            EnhancedClientSession self,
            string deletedName)
        {
            if (self?.Player == null || string.IsNullOrWhiteSpace(deletedName))
                return;

            var nameBytes = ClientTextEncoding.GetBytes(deletedName);
            var w = new GamePacketWriter();
            w.WriteInt32(2);                    // subcmd=2 删除
            w.WriteUInt32(1);                   // count=1
            w.WriteByte(1);                     // 大区 sR=1（与列表 entry 一致）
            w.WriteUInt32((uint)nameBytes.Length);
            w.WriteBytes(nameBytes);            // name

            var body = w.ToArray();
            await self.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(0x00, 0x0111, body));
            FileLogger.Log(
                $"[UnitedFriend] {GetPlayerName(self)} 好友删除 subcmd=2 "
                + $"name=\"{deletedName}\" body({body.Length}B): {BitConverter.ToString(body)}");
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
        /// 构造 0x0112 上下线聊天通知 body（字段布局见设计文档 §4.4）：
        /// channel≠0 → "X 进入频道"；channel==0 → "X 退出频道"。
        /// oF 恒 0x00 → 不进黑名单（黑名单未实现，见设计文档 §8）。
        /// </summary>
        private static byte[] BuildChatNoticeBody(
            ushort channel,
            string name)
        {
            var w = new GamePacketWriter();
            w.WriteUInt16(channel);              // 频道：0=退出频道, 真实频道=进入频道
            w.WriteByte(1);                      // 大区 sR=1（同区命中 0x29E/0x29F 文案）
            var nameBytes = ClientTextEncoding.GetBytes(name);
            w.WriteUInt32((uint)nameBytes.Length);
            w.WriteBytes(nameBytes);             // name
            w.WriteByte(0);                      // oF=0x00 → 不进黑名单
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

        /// 0x0122 添加好友。名字段为 GBK。
        public static async Task HandleAddUnitedServerFriend(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body,
            ISessionDirectory sessionDirectory)
        {
            ushort targetUserId = 0xFFFF;
            byte operation = 0x01;
            uint nameLen = 0;
            string targetName = "";
            if (body != null && body.Length >= 7)
            {
                targetUserId = BitConverter.ToUInt16(body, 0);
                operation = body[2];
                nameLen = BitConverter.ToUInt32(body, 3);
                if (nameLen > 0 && body.Length >= 7 + (int)nameLen)
                    targetName = ClientTextEncoding.GetString(body, 7, (int)nameLen);
            }

            FileLogger.Log(
                $"[UnitedFriend] ADD_UNITED_SERVER_FRIEND cid={session?.Player?.CharacterId ?? 0} "
                + $"targetUid=0x{targetUserId:X4} op={operation} nameLen={nameLen} name=\"{targetName}\" "
                + $"body({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            // 加好友校验（失败 → 2B ACK [0x00, errorCode]，errorCode→弹窗文案见 §7.5）：
            //   ① 目标名非空（→0x15 该角色不存在）② 非自加（→0x01 静默）③ 好友数<100（→0x04 已满）
            //   ④ 目标存在（CharacterRepository.GetByName, delete_flag=0 →0x15 不存在）。
            //   通过 → 记单向关系并持久化（§1.2），成功 ACK(13B) 后全量刷新好友列表（不只刷新新增的那个）。
            var selfName = GetPlayerName(session);

            byte addRejectCode = 0;
            string addRejectReason = null;

            if (string.IsNullOrWhiteSpace(targetName))
            {
                addRejectCode = 0x15;      // 该角色不存在
                addRejectReason = "目标角色名为空";
            }
            else if (!string.IsNullOrEmpty(selfName)
                && string.Equals(selfName, targetName,
                    StringComparison.OrdinalIgnoreCase))
            {
                addRejectCode = 0x01;      // 无文案（静默不弹，§7.5）
                addRejectReason = "不能添加自己";
            }
            else if (!string.IsNullOrEmpty(selfName))
            {
                var friendCount = GetFriends(selfName).Count;
                if (friendCount >= 100
                    && !IsFriend(selfName, targetName))
                {
                    addRejectCode = 0x04;  // 好友已满，无法继续添加好友
                    addRejectReason =
                        $"好友数已达上限 100 (count={friendCount})";
                }
                else if (CharacterRepository.GetByName(targetName) == null)
                {
                    addRejectCode = 0x15;  // 该角色不存在
                    addRejectReason = $"目标角色 \"{targetName}\" 不存在";
                }
            }
            else
            {
                addRejectCode = 0x15;      // 会话未进入游戏/无名
                addRejectReason = "自身角色名为空";
            }

            if (addRejectCode != 0)
            {
                byte[] rejectBody = new byte[] { 0x00, addRejectCode };
                await session.SendPacketAsync(
                    GamePacketEnvelopeBuilder.Build(0x01, header.type, rejectBody));
                FileLogger.Log(
                    $"[UnitedFriend] ADD_UNITED_SERVER_FRIEND ACK (reject) "
                    + $"errorCode=0x{addRejectCode:X2} reason=\"{addRejectReason}\" "
                    + $"name=\"{targetName}\" body(2B): {BitConverter.ToString(rejectBody)}");
                return;
            }

            try
            {
                // 校验已保证 selfName/targetName 非空且非自加，记录单向关系。
                RecordFriendship(selfName, targetName);
            }
            catch (Exception recordEx)
            {
                FileLogger.Log(
                    $"[UnitedFriend] ADD_UNITED_SERVER_FRIEND record friendship "
                    + $"failed: {recordEx.Message}");
            }

            // 成功 ACK 固定 13B = [success_flag:1B=0x01][3×int32]（反编译结论，见设计文档 §7.4）：
            //   dispatcher 先消费 body[0]：0x00→失败（按 body[1] 弹窗，不调 0xD13F30）；非0→成功，
            //   剩余 12B 传 0xD13F30 parse_int32×3。字段A 有效范围 [0x1bc6..0x1bf5] / [0x1c15..0x1c19]
            //   （超界静默）；B-C>6 走设 flag=1（激活好友面板），[1..6] 走聊天消息路径，<=0 不显示。
            //   选 A=0x1bc6(范围下界)、B=10、C=2 → B-C=8 走设 flag=1 路径。
            int fieldA = 0x1bc6;                       // 有效范围下界值
            int fieldB = 10;                            // B-C=8 > 6 → 设 flag=1 路径
            int fieldC = 2;                             // B-C=8
            var ackBody = new byte[13];
            ackBody[0] = 0x01;  // success_flag = 成功
            Buffer.BlockCopy(BitConverter.GetBytes(fieldA), 0, ackBody, 1, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(fieldB), 0, ackBody, 5, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(fieldC), 0, ackBody, 9, 4);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, ackBody));

            FileLogger.Log(
                $"[UnitedFriend] ADD_UNITED_SERVER_FRIEND ACK (success) sent body({ackBody.Length}B): {BitConverter.ToString(ackBody)} "
                + $"success_flag=0x01 fieldA=0x{fieldA:X8} fieldB={fieldB} fieldC={fieldC} B-C={fieldB - fieldC} "
                + $"(13B=[0x01][3×int32]; dispatcher消费flag→0xD13F30 parse 12B; B-C>6→设flag=1; 随后推 type=0x0111 刷新列表)");

            // 为何不用 0x0124(PVP_BUDDY_CONN_LIST) 官方推送路径：buddy_list 初始容量=0，
            // count=1 静默失败，需 count=2 触发 resize 才显示（反汇编结论，见设计文档 §7.3/§8）。
            // 改用 0x0111 subcmd=0 连发两遍全量刷新更可靠。全量刷新：只刷新增那个会把
            // 面板刷成一行，须按持久化关系（united_friend_relations）全量刷新。
            if (!string.IsNullOrEmpty(selfName))
            {
                // 被加好友在线且同频道 → 给 self 推其 USERINFO 实体（好友面板图标变绿）。
                // 与登录 hook 方向2 同构；不在线/不同频道则无操作。
                try
                {
                    await NotifyFriendAddedAsync(
                        session, targetName, sessionDirectory);
                }
                catch (Exception addEntityEx)
                {
                    FileLogger.Log(
                        $"[UnitedFriend] ADD_UNITED_SERVER_FRIEND 推被加好友实体失败: {addEntityEx.Message}");
                }

                try
                {
                    var allFriends = GetFriends(selfName);
                    await SendFriendListAsync(
                        session,
                        sessionDirectory,
                        allFriends);
                    FileLogger.Log(
                        $"[UnitedFriend] ADD_UNITED_SERVER_FRIEND 好友列表全量刷新 "
                        + $"count={allFriends.Count} names=[{string.Join(", ", allFriends)}]");
                }
                catch (Exception listEx)
                {
                    FileLogger.Log(
                        $"[UnitedFriend] ADD_UNITED_SERVER_FRIEND friend-list " +
                        $"refresh failed: {listEx.Message}");
                }
            }
        }

        /// 0x0123 删除好友。名字段为 GBK，无长度前缀，读到 body 尾。
        public static async Task HandleDeleteUnitedServerFriend(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body,
            ISessionDirectory sessionDirectory)
        {
            byte operation = 0x01;
            uint targetCharId = 0;
            string targetName = "";
            if (body != null && body.Length >= 5)
            {
                operation = body[0];
                targetCharId = BitConverter.ToUInt32(body, 1);
                if (body.Length > 5)
                    targetName = ClientTextEncoding.GetString(body, 5, body.Length - 5);
            }

            FileLogger.Log(
                $"[UnitedFriend] DELETE_UNITED_SERVER_FRIEND cid={session?.Player?.CharacterId ?? 0} "
                + $"targetCharId={targetCharId} op={operation} name=\"{targetName}\" "
                + $"body({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            var selfName = GetPlayerName(session);

            // 目标名兜底：body name 为空/乱码时按 targetCharId 反查权威名
            if (string.IsNullOrWhiteSpace(targetName) && targetCharId != 0)
            {
                try
                {
                    var targetRec = CharacterRepository.GetById((int)targetCharId);
                    if (targetRec != null)
                        targetName = targetRec.DisplayName;
                }
                catch (Exception nameEx)
                {
                    FileLogger.Log(
                        $"[UnitedFriend] DELETE_UNITED_SERVER_FRIEND 按 characNo={targetCharId} 反查失败: {nameEx.Message}");
                }
            }

            byte rejectCode = 0;
            string rejectReason = null;

            if (string.IsNullOrWhiteSpace(targetName))
            {
                rejectCode = 0x15;      // 该角色不存在（目标名/characNo 均无法解析）
                rejectReason = $"目标角色无法解析 (characNo={targetCharId})";
            }
            else if (string.IsNullOrEmpty(selfName))
            {
                rejectCode = 0x15;      // 会话未进入游戏/无名
                rejectReason = "自身角色名为空";
            }
            else if (!IsFriend(selfName, targetName))
            {
                // 对方不在我的好友名单中。该文案(0x18E)由客户端本地检查弹出，
                // 服务端无对应 errorCode 映射，用 0x05（未注册码→客户端静默）防御性回包。
                rejectCode = 0x05;
                rejectReason = $"\"{targetName}\" 不在 \"{selfName}\" 的好友名单中";
            }

            if (rejectCode != 0)
            {
                byte[] rejectBody = new byte[] { 0x00, rejectCode };
                await session.SendPacketAsync(
                    GamePacketEnvelopeBuilder.Build(0x01, header.type, rejectBody));
                FileLogger.Log(
                    $"[UnitedFriend] DELETE_UNITED_SERVER_FRIEND ACK (reject) "
                    + $"errorCode=0x{rejectCode:X2} reason=\"{rejectReason}\" "
                    + $"name=\"{targetName}\" body(2B): {BitConverter.ToString(rejectBody)}");
                return;
            }

            try
            {
                var removed = RemoveFriendship(selfName, targetName);
                if (!removed)
                {
                    // 竞态兜底：校验后关系被并发移除，按不存在处理
                    byte[] rejectBody = new byte[] { 0x00, 0x05 };
                    await session.SendPacketAsync(
                        GamePacketEnvelopeBuilder.Build(0x01, header.type, rejectBody));
                    FileLogger.Log(
                        $"[UnitedFriend] DELETE_UNITED_SERVER_FRIEND ACK (race) "
                        + $"\"{selfName}\" -/-> \"{targetName}\" 并发移除，按不存在处理");
                    return;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[UnitedFriend] DELETE_UNITED_SERVER_FRIEND RemoveFriendship 异常: {ex}");
                byte[] rejectBody = new byte[] { 0x00, 0x05 };
                await session.SendPacketAsync(
                    GamePacketEnvelopeBuilder.Build(0x01, header.type, rejectBody));
                return;
            }

            // 成功 ACK：与 ADD 同构 13B = [0x01][3×int32]（同构原因见设计文档 §4.2 / §7.4）。
            // 复用已验证字段值：B-C=8>6 保持 friend_manager flag=1。
            int fieldA = 0x1bc6;
            int fieldB = 10;
            int fieldC = 2;
            var ackBody = new byte[13];
            ackBody[0] = 0x01;  // success_flag = 成功
            Buffer.BlockCopy(BitConverter.GetBytes(fieldA), 0, ackBody, 1, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(fieldB), 0, ackBody, 5, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(fieldC), 0, ackBody, 9, 4);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, ackBody));
            FileLogger.Log(
                $"[UnitedFriend] DELETE_UNITED_SERVER_FRIEND ACK (success) sent "
                + $"body({ackBody.Length}B): {BitConverter.ToString(ackBody)} "
                + $"(13B=[0x01][3×int32], fieldA=0x{fieldA:X8} B={fieldB} C={fieldC}, "
                + $"B-C={fieldB - fieldC}>6 → friend_manager flag=1)");

            // 列表刷新：先 subcmd=2 删被删好友节点（subcmd=0 不清陈旧节点，必须走 subcmd=2），
            // 再按剩余好友 subcmd=0 全量刷新两遍（重建/更新在线状态）。
            if (!string.IsNullOrEmpty(selfName))
            {
                try
                {
                    await SendFriendDeletedAsync(session, targetName);
                    var allFriends = GetFriends(selfName);
                    await SendFriendListAsync(
                        session,
                        sessionDirectory,
                        allFriends);
                    FileLogger.Log(
                        $"[UnitedFriend] DELETE_UNITED_SERVER_FRIEND 好友列表刷新 "
                        + $"removed=\"{targetName}\" remain={allFriends.Count} "
                        + $"names=[{string.Join(", ", allFriends)}]");
                }
                catch (Exception listEx)
                {
                    FileLogger.Log(
                        $"[UnitedFriend] DELETE_UNITED_SERVER_FRIEND friend-list " +
                        $"refresh failed: {listEx.Message}");
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;

namespace DfoServer.Game.Party
{
    /// <summary>组队上限与常量。DNF 一支队伍最多 4 人。</summary>
    public static class PartyConstants
    {
        public const int MaxMembers = 4;

        public static bool IsSupportedCapacity(int capacity)
            => capacity >= 1 && capacity <= MaxMembers;
    }

    /// <summary>
    /// 一名队伍成员的快照(格式无关)。UserId = (ushort)CharacterId, 是城镇/组队封包里的网络身份。
    /// 仅缓存构建下发封包所需的身份字段; 不持有 session 引用(由 PartyManager/handler 用 SessionId 解析)。
    /// </summary>
    public sealed class PartyMember
    {
        public ushort UserId { get; set; }
        public int CharacterId { get; set; }
        public Guid SessionId { get; set; }
        public string Name { get; set; } = string.Empty;
        public byte Level { get; set; }
        public byte Job { get; set; }

        /// <summary>队内槽位 0..3(加入顺序分配, 成员离队后不重排以保持其它成员槽位稳定)。</summary>
        public byte SlotIndex { get; set; }

        // P2P 端点(用于 PARTY IP INFO 0x0B, 让队友互相直连/清"连接中")。BuildMember 从会话 TCP 远端填。
        /// <summary>局域网 IP 四字节(octets a.b.c.d, 网络序)。默认回环。</summary>
        public byte[] IpBytes { get; set; } = new byte[] { 127, 0, 0, 1 };
        /// <summary>客户端 UDP P2P 端口。默认 10000(DNF 常用基/客户端 0x0351 实测报 0x2710)。</summary>
        public ushort P2pPort { get; set; } = 10000;
        /// <summary>账号 id(0x0B 里下发, 客户端是否强校未定)。</summary>
        public uint AccId { get; set; }
    }

    /// <summary>
    /// 一支队伍的服务端状态(格式无关)。生命周期由 <see cref="PartyManager"/> 管理。
    /// 对应 df_game_r 的 CParty 语义(参考, 非照搬): 队长 + 成员槽 + 队伍索引 + 单人标志。
    /// </summary>
    public sealed class Party
    {
        private readonly List<PartyMember> _members = new List<PartyMember>(PartyConstants.MaxMembers);

        public int PartyId { get; }

        /// <summary>队长的 UserId。队长离队时转移给下一名成员; 无人则解散。</summary>
        public ushort LeaderUserId { get; set; }

        /// <summary>队伍设置(来自 SET_PARTY_INFO)。字节语义待 86jp 确认, 这里只保留可选名 + 三个设置字段。</summary>
        public string PartyName { get; set; } = string.Empty;
        public byte SettingA { get; set; }
        public ushort SettingB { get; set; }
        public byte SettingC { get; set; }

        // A21 规范化 SET_PARTY_INFO 的 raw[2] 是人数上限；2/3/4 已由
        // 当前精确客户端的创建/修改请求实机确认。其余字段仍保持 opaque。
        public byte TitleIndex { get; set; }
        public byte[] TitleBytes { get; set; } = System.Array.Empty<byte>();
        public byte UserMax { get; set; } = 4;
        public ushort DungIndex { get; set; }
        public byte DungDiffi { get; set; }

        // A21 SET_PARTY_INFO supplies these 12 direct fields. PARTY_INFO(type
        // 0/1) writes info0, a conditional empty dstr when info0 is zero, then
        // info1..info11. Most individual field meanings remain unknown.
        public byte[] PartyInfoBlock { get; set; } =
            new byte[] { 0, 0, 4, 0, 0, 0, 0, 5, 0, 0, 0xFF, 0xFF };

        /// <summary>单人游戏(自建 1 人队); 进副本单刷时用。</summary>
        public bool IsSinglePlay { get; set; }

        public IReadOnlyList<PartyMember> Members => _members;

        public Party(int partyId)
        {
            PartyId = partyId;
        }

        public int Count => _members.Count;
        public int Capacity => PartyConstants.IsSupportedCapacity(UserMax)
            ? UserMax
            : PartyConstants.MaxMembers;
        public bool IsFull => _members.Count >= Capacity;
        public bool IsEmpty => _members.Count == 0;

        public bool IsLeader(ushort userId) => LeaderUserId == userId && Contains(userId);

        public bool Contains(ushort userId) => GetMember(userId) != null;

        public PartyMember GetMember(ushort userId)
        {
            foreach (var m in _members)
                if (m.UserId == userId)
                    return m;
            return null;
        }

        /// <summary>分配一个未占用的槽位 0..3; 满则返回 -1。</summary>
        private int AllocateSlot()
        {
            var used = new bool[PartyConstants.MaxMembers];
            foreach (var m in _members)
                if (m.SlotIndex < PartyConstants.MaxMembers)
                    used[m.SlotIndex] = true;
            for (byte i = 0; i < PartyConstants.MaxMembers; i++)
                if (!used[i])
                    return i;
            return -1;
        }

        /// <summary>加入一名成员。满员或重复加入返回 false。</summary>
        public bool TryAddMember(PartyMember member)
        {
            if (member == null) return false;
            if (IsFull) return false;
            if (Contains(member.UserId)) return false;
            var slot = AllocateSlot();
            if (slot < 0) return false;
            member.SlotIndex = (byte)slot;
            _members.Add(member);
            return true;
        }

        /// <summary>把指定成员移到槽位 0(与当前占用 slot0 的成员交换)。
        /// 委托队长后调用: 此客户端以【slot0=队长】判定(PARTY_INFO 无独立队长字段), 需把新队长排到 slot0 才显示正确。</summary>
        public void MoveToSlotZero(ushort userId)
        {
            var target = GetMember(userId);
            if (target == null || target.SlotIndex == 0) return;
            var oldSlot = target.SlotIndex;
            PartyMember atZero = null;
            foreach (var m in _members)
                if (m.SlotIndex == 0) { atZero = m; break; }
            target.SlotIndex = 0;
            if (atZero != null) atZero.SlotIndex = oldSlot;
        }

        /// <summary>移除一名成员。不存在返回 false。队长转移/解散由 PartyManager 决策, 不在此处理。</summary>
        public bool RemoveMember(ushort userId)
        {
            for (var i = 0; i < _members.Count; i++)
            {
                if (_members[i].UserId == userId)
                {
                    _members.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>成员按槽位升序返回(下发名册用固定顺序)。</summary>
        public List<PartyMember> MembersBySlot()
        {
            var list = new List<PartyMember>(_members);
            list.Sort((a, b) => a.SlotIndex.CompareTo(b.SlotIndex));
            return list;
        }

        // Called by PartyManager while holding its lock. Packet builders consume
        // this detached copy so a concurrent join/leave cannot produce a roster
        // from one generation and relay ports from another.
        internal Party CreateSnapshot()
        {
            var snapshot = new Party(PartyId)
            {
                LeaderUserId = LeaderUserId,
                PartyName = PartyName,
                SettingA = SettingA,
                SettingB = SettingB,
                SettingC = SettingC,
                TitleIndex = TitleIndex,
                TitleBytes = TitleBytes == null
                    ? System.Array.Empty<byte>()
                    : (byte[])TitleBytes.Clone(),
                UserMax = UserMax,
                DungIndex = DungIndex,
                DungDiffi = DungDiffi,
                PartyInfoBlock = PartyInfoBlock == null
                    ? System.Array.Empty<byte>()
                    : (byte[])PartyInfoBlock.Clone(),
                IsSinglePlay = IsSinglePlay,
            };

            foreach (var member in _members)
            {
                snapshot._members.Add(new PartyMember
                {
                    UserId = member.UserId,
                    CharacterId = member.CharacterId,
                    SessionId = member.SessionId,
                    Name = member.Name,
                    Level = member.Level,
                    Job = member.Job,
                    SlotIndex = member.SlotIndex,
                    IpBytes = member.IpBytes == null
                        ? null
                        : (byte[])member.IpBytes.Clone(),
                    P2pPort = member.P2pPort,
                    AccId = member.AccId,
                });
            }

            return snapshot;
        }
    }
}

# fix：迁移并修复 A21 每日挑战任务完整闭环

## 背景

S4A21 服务端代码主要由 S4A12 迁移而来，但两个客户端版本在每日挑战 PVF 配置、`0x0286` 数据结构及任务完成 ACK 布局上存在差异。

直接沿用 A12 实现后，实际出现以下问题：

- 挑战任务窗口无法打开；
- 单项挑战领奖时客户端闪退；
- 领奖成功后，按钮在线状态仍显示“领取奖励”，重新选角才恢复为“完成”；
- “推荐地下城击杀领主”任务不会推进；
- 组奖励已经显示 `2/2`，领取时却提示“当前状态无法获得奖励（0）”；
- 普通任务完成 ACK 中的字段如果继续错误投影金币，可能触发大量 `0x0021 SET_QUEST_TRIGGER` 回显。

本 MR 根据当前 A21 PVF、实机抓包、客户端运行时逆向和实际游玩验证，完成每日挑战任务的生成、初始化、权威进度、单项奖励、组奖励、每日重置及持久化闭环。

## A21 与 A12 的关键差异

### `0x0286 DAILY_CHALLENGE`

当前 A21 PVF：

`etc/dailychallengetable.etc`

包含 5 个普通挑战分组，没有 A12 的 `[special challenge]`。

客户端逆向确认：

- `0x0286` 首字段为角色等级；
- 普通任务条目顺序为任务 ID、剩余值、目标值；
- 领奖 flag 数量必须恰好为 5；
- 客户端对 flag 数量执行 `length == 5` 硬校验；
- A12 的第 6 个特殊任务 flag、tail count 和 tail ID 列表不能迁入 A21。

此前窗口无法打开的根因，就是 A12 特殊任务结构与 A21 客户端的 5 flag 布局不兼容。

### `0x0287 DAILY_CHALLENGE_CLEAR_DUNGEON`

A21 客户端的 `0x0287` handler 只读取一个 little-endian `UInt32`。

该值按不透明的稳定完成标记处理：

- 不解释为地下城 ID；
- 不解释为累计次数；
- 同一结算事件重复投影同一标记；
- 有效推荐地下城结算时先发送完整 `0x0286`，再发送 `0x0287`。

领主击杀类挑战只刷新完整 `0x0286`，不发送属于地下城通关边沿的 `0x0287`。

## 修改内容

### 按当前 A21 PVF 生成每日挑战

- 从 `etc/dailychallengetable.etc` 读取挑战分组、等级、槽位和奖励；
- 根据角色、日期、分组和槽位稳定选择当天任务；
- 在同一次生成计划中排除已经选中的任务 ID，避免跨槽位重复任务导致领取歧义；
- 根据 QST `[level]` 再次过滤候选任务；
- 防止低等级角色生成高等级挑战 QST；
- 目标值从对应 QST/PVF 中解析，不在服务端重复硬编码；
- 同一天重复登录保留任务及进度；
- 跨日重新生成并清理进度、领奖和事件状态；
- 自动修复旧版本已经生成的等级不合法账本。

### 角色选择初始化

角色选择时下发完整 `0x0286`：

- 首字段为真实角色等级；
- 只包含当前 A21 的普通挑战分组；
- 条目顺序固定为剩余值、目标值；
- 固定下发 5 个组奖励 flag；
- 不携带 A12 特殊任务状态和 tail list。

### 推荐地下城通关进度

推荐地下城挑战由服务端权威副本结算推进：

- 使用普通成功 settlement 作为权威事件；
- 通过角色等级、地下城等级适配和最低难度判断；
- 使用稳定的 settlement source event ID 去重；
- 同一结算事件重复处理不会重复增加；
- 发生相关匹配或重试时允许重新投影完整快照；
- 条目完成后的全新结算事件不再重复下发无变化快照；
- 实际递减后按 `0x0286 → 0x0287` 顺序通知客户端。

客户端对应的 `0x0021` 上报只读取服务端当前值，不再自行推进该类任务。

### 推荐地下城领主击杀

当前 PVF 的领主任务使用以下 QST 目标结构：

```text
dungeon selector     = -3
minimum difficulty   = -1 或指定难度
monster selector     = -3
required count       = 3 或 5
```

其中：

- 第一个 `-3` 表示推荐等级地下城；
- monster `-3` 表示任意领主；
- A21 当前领主 actor type 为 `3`。

本 MR 将其接入 `DungeonKillApplicationService` 的 canonical actor-death event：

- 只接受服务端确认的领主死亡；
- 只接受允许普通任务目标推进的副本和 actor；
- 校验推荐等级地下城及最低难度；
- 使用 canonical `SourceEventId` 持久化去重；
- 冻结队伍事件可以按参与者分别投影；
- 数据库或通知失败时保留事件重试能力；
- 已提交事件重试不会重复计数；
- 每次相关击杀后发送完整 `0x0286`；
- 只在实际推进或同一已提交事件重试时发送 `0x0286`，任务完成后的新击杀不再重复发送；
- 客户端 `0x0021` 回显不能重复增加进度。

### 普通任务完成类挑战

“完成主线任务”等挑战在普通任务 FINISH 的同一事务内推进：

- 按 QST grade selector 匹配任务类型；
- 使用经过校验的 `completionCount`；
- 普通任务完成、每日挑战进度和奖励结算保持同一事务；
- 客户端后续 `0x0021` 只作为服务端状态回读。

### 修复 `FINISH_QUEST` ACK 完成次数语义

客户端逆向确认，A21 `0x0022 FINISH_QUEST` 成功 ACK 在 Exp 后读取的第二个 `UInt32` 是：

`completionCount`

而不是金币。

修复后的关键布局：

```text
success
questId
completionType
exp
completionCount
consumedEntryCount
consumedEntries（每条 7 字节）
chainType
insertedRewardCount
insertedRewards
```

金币继续通过后续 `itemId=0` 奖励记录投影。

这同时修复了把金币数值解释成任务完成次数后，客户端高频发送大量 `0x0021` 的问题。

### 修复单项挑战领奖闪退

A21 FINISH 成功分支要求：

- consumed entry 固定为 7 字节；
- consumed count 后必须存在独立的 `chainType`；
- 即使没有消耗物品，也必须写入 `chainType=0`；
- chain 后才能写 inserted reward count。

此前缺少独立 chain 字节时，首个奖励数量可能被解释为 chain type，进入错误客户端分支并造成越界消费、闪退。

本 MR 按 A21 实读布局修复序列化。

### 单项挑战奖励闭环

单项领奖复用现有 `QuestCompletionApplicationService`：

- 校验任务已完成且尚未领取；
- 解析 QST 奖励；
- 处理材料消耗和背包容量；
- 发放物品、金币和经验；
- 使用当前 session-owned `InventoryLease`；
- 组奖励在同一事务内从数据库读取账号归属和权威角色等级，不按会话缓存等级选档；
- 当前组奖励回滚边界只接受实体背包物品和主虚拟数量，其他奖励类型在闭环前拒绝；
- 背包、任务完成 flag、领奖记录和奖励在同一 SQLite 事务提交；
- 失败时回滚背包内存状态；
- 重复请求、请求重放和重新登录不能重复发奖。

QST 奖励仍使用普通任务经验公式。

例如角色 16 级领取 QST 14561 时，当前 PVF 定义：

```text
物品：10099414 × 1
经验：31 Exp
```

实机出现的 31 Exp 符合 PVF 和 `questParameter` 公式，并非错误奖励。

### 修复领奖后按钮不立即变为“完成”

原流程已经正确提交数据库和领奖记录，但在线只发送了 `0x0286`。

客户端“完成”按钮还依赖完整的：

`NotiPacketTypeA21.CLEAR_QUEST_LIST / 0x0164`

因此表现为：

- 在线仍显示“领取奖励”；
- 重新选角后初始化补发 `0x0164`，才显示“完成”。

修复后单项领奖成功顺序为：

1. `FINISH_QUEST` ACK；
2. 普通奖励和后继任务通知；
3. 完整 `CLEAR_QUEST_LIST`；
4. 最新 `0x0286` 挑战快照。

在线领取后按钮立即变为“完成”，不再依赖重新选角。

### 修复组奖励 `2/2` 无法领取

A21 PVF 的 group 0 没有显式配置：

`[reward challenge num]`

客户端将缺省值解释为 `2`，所以 UI 显示：

`完成2个以上挑战任务（2/2）`

服务端此前错误回退为当前等级活动槽位总数。低等级角色有 3 个活动槽，因此即使客户端显示 `2/2`，服务端仍以 `2/3` 判定为未完成并返回失败。

修复后：

- group 0 缺省门槛按 A21 客户端语义固定为 2；
- 显式配置 `[reward challenge num]` 的其他组继续使用 PVF 数值；
- 只完成两项、第三项未完成时也能正常领取；
- 组奖励仍保持事务、Inventory lease 和重复领取幂等。

### 审查补强与无关回退修复

- `DailyChallengeRepository` 保持为挑战账本唯一写入 owner；
- 通用 `SqliteCharacterStateRepository.SaveFlags` 不再删除、重建挑战 group/entry/tail，避免教程 flag 31 保存时通过外键级联清除单项领取和事件去重记录；
- 缺失条目告警缓存改为服务实例内按任务 ID 去重，避免进程生命周期内按角色无限增长；
- 保留主线已经验证的 `DUNGEON_INFO` 领主坐标投影，撤销挑战提交中无关的全局 `FF/FF` 回退，并同步修正陈旧测试预期。

## 数据库迁移

当前数据库基线保持：

```text
86jp-database-v1
baseline schema v1
```

连续迁移版本升级到 schema v6。

### `character_daily_challenge_entry_claims`

保存单项挑战奖励领取记录。

唯一约束：

```text
character_id
group_index
entry_index
```

用于阻止：

- 重复点击；
- 请求重放；
- 并发领取；
- 重新登录后的重复发奖。

### `character_daily_challenge_progress_events`

保存推荐地下城通关及领主击杀已经消费的权威事件。

唯一约束：

```text
character_id
source_event_id
group_index
entry_index
```

用于保证同一副本或领主事件对同一角色、同一挑战条目只能推进一次。

新库由当前 `item_schema.sql` 直接创建完整 schema v6；既有 schema v5 数据库通过连续迁移升级。业务 handler 不执行运行时隐式建表。

## 协议与资源影响

- A21 CMD/NOTI 名称及 opcode 统一来自 `PacketTypesA21.cs`；
- 不修改、不提交客户端二进制；
- 不修改、不提交 `Script.pvf`；
- 不迁入 A12 `[special challenge]`；
- 不提交 SQLite 玩家数据库、WAL/SHM、日志、抓包、Debug/Release 输出或运行备份；
- 临时强制注入 QST 14522 只用于实机验证，没有进入代码或提交。

## 自动化验证

显式设置当前 A21 PVF：

```powershell
$env:PVF_ARCHIVE_PATH = (Resolve-Path "Server\DfoServer\Data\Pvf\Script.pvf").Path
```

执行最终构建：

```powershell
dotnet build Server/DfoServer.sln -c Debug --no-restore -t:Rebuild
```

结果：

```text
生成成功
0 个警告
0 个错误
```

执行挑战任务专项测试：

```powershell
Server\DfoServer\bin\Debug\DfoServer.exe --selftest-a21-daily-challenge
```

结果：

```text
A21_DAILY_CHALLENGE PASS
```

专项覆盖：

- A21 `0x0286` 五 flag 布局；
- A12 special state 排除；
- `0x0287` 单 UInt32 token；
- QST 等级过滤；
- 日账本生成、保留、跨日重建；
- 多角色、多日期、多等级生成任务唯一性；
- 推荐地下城权威结算及事件幂等；
- 领主击杀类型、推荐等级判断和事件幂等；
- 服务端权威任务的客户端 echo 防重；
- `FINISH_QUEST` completionCount；
- 7 字节 consumed entry 和独立 chain；
- 单项领奖事务及重复领取；
- 在线 `ACK → CLEAR_QUEST_LIST → 0x0286` 顺序；
- group 0 缺省 `2/2` 门槛；
- 组奖励成功 ACK 及重复领取。
- 通用教程/初始化 flag 保存不破坏挑战条目、单项领取和事件去重账本；
- 已完成条目只对原提交事件重试恢复快照，不对后续全新事件重复通知；
- `DUNGEON_INFO` 继续保留领主坐标。

执行全量回归：

```powershell
Server\DfoServer\bin\Debug\DfoServer.exe --selftest-all
```

结果：

```text
total=14
pass=14
fail=0
```

最终再次执行 Rebuild，结果仍为：

```text
0 warning
0 error
```

## A21 实机验证

当前 A21 客户端已完成以下验证：

- 挑战任务窗口可以正常打开；
- 单项挑战任务可以完成和领取；
- 领奖弹窗正确显示 QST 物品和公式经验；
- 单项领奖不再导致客户端闪退；
- 领取后按钮立即变为“完成”，无需重新选角；
- 组奖励在 `2/2` 状态可以正常领取；
- 推荐地下城领主击杀后，挑战进度正常增加；
- 服务端重启和重新登录后，已提交的完成及领奖状态可以恢复。

## 已知边界

- A21 当前 PVF 没有 A12 的特殊挑战任务，因此不实现或投影第六条 special challenge；
- `0x0287` 没有业务 ACK，断线发生在通知写入后的极端 UI 时序仍以重新登录完整快照恢复；
- 本 MR 保证当前单进程、SQLite 事务和请求期内存回滚边界，不宣称跨进程崩溃场景下的通用 exactly-once。

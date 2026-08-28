# ClockService 时间队列

`ClockService` 是服务端进程内时间队列, 用于在线通知和短生命周期的运行时控制。它不是持久化系统。任何需要跨重启保持正确的功能, 都必须把权威状态落库, 并在服务启动、玩家登录或会话恢复时按持久化状态重建 timer。

## 硬性规则

- 本服务只解决“到点主动给在线玩家做某件事”, 例如推送、广播、在线巡检、短时阶段推进。
- 本服务不往数据库写任何东西, 也不记录“上次执行到哪”。停机、卡顿或进程重启期间错过的时间点不补触发; 连续错过多次只触发一次。
- 接入功能必须做到: 时钟一次都没响, 玩家登录、使用、进本、领奖、查看状态时拿到的状态也照样正确。
- 回调里只允许读数据、顺带结算已有状态、检查条件、给在线玩家发包。不允许凭空创造奖励、次数、货币或进度。
- 需要发道具、补偿、次数重置、虚弱恢复、团本持久阶段等功能, 必须先设计为落库记账或读库结算。`ClockService` 最多提醒在线玩家或刷新客户端 UI。
- 回调不会与自己并发; 上一轮没跑完会跳过本轮检查。回调异常只写日志, 不影响其他回调。耗时或异步操作要自行派发, 并在业务层做幂等。
- 会写同一 SQLite 的周期回调不得用 `Task.Run` 脱离 Clock 顺序执行；否则后续周期回调会与其并行开启写事务。需要异步通知时，先串行完成状态结算，再使用 `ScheduleOneShotAsync` 等可观察入口处理通知。
- 系统时间被往回拨时, 周期 timer 会重新对表, 当轮不触发。被拨回去的时间点随时间再次到来可能再触发一次; 在上面规则约束下这应该是无害的。
- 时间基准使用北京时间 UTC+8, 与当前项目每日重置 06:00 口径保持一致。

## 模型

- 内部使用按 UTC 到期时间排序的最小堆, 对齐旧服 `TimerQueue` 的核心形态。
- 已取消、已被同名新 timer 替换的一次性 timer 会留在堆里, 等到浮到堆顶时惰性丢弃。
- 当惰性失效的一次性 timer 积累到阈值后, 会重建堆并移除旧节点, 避免团本/心跳这类频繁替换长延迟 timer 的场景让堆长期膨胀。
- 调度器只维护一个系统 `Timer`, 始终指向当前最近到期的队列项。
- 每分钟、每日、每周这类周期任务会合并错过的间隔。卡顿很久后只触发一次, 不补放每个错过的时间点。
- 回调在调度锁外执行。同步/异步回调异常都会被捕获并写日志。

## 适用场景

适合使用 `ClockService` 的场景:

- 团本 ready 倒计时、攻坚阶段切换。
- 团本、队伍、副本内短时超时。
- 在线宠物运行时检查和死亡 timer。
- 面向在线玩家的 UI 通知或发包刷新。

不要把它作为这些状态的唯一真相来源:

- 道具奖励、邮件、货币、次数限制、计数器。
- 每日/每周重置正确性。
- 拍卖、会员、租赁、团本进度等必须跨进程重启存活的状态。

这些功能应先写入持久化状态。时间队列最多负责提醒在线玩家, 或推进可丢弃的会话态。登录或启动时, 需要从持久化状态重建仍然有效的 timer。

## 旧服依据

旧台服 `df_game_r` 低于当前项目目标版本, 只能作为行为参照, 不能不加区分地照搬。

| 旧服函数 | 地址 | 结论 | 当前项目接入边界 |
| --- | --- | --- | --- |
| `TimerQueue::InsertTimer` / `GetTimerMess` | `0x8630cec` / `0x8630ecc` | 旧服用 `std::priority_queue<TimerEntry>` 做进程内到期队列 | `ClockService` 采用堆模型合理 |
| `TimerCheckConn::dispatch_sig` | `0x8632bbc` | 心跳/连接检查用 timer, 并校验 user `unique_id` | 心跳可接入, 必须校验 session/version |
| `TimerFatigueReset::dispatch_sig` | `0x8633750` | 旧服用固定时刻 timer 触发疲劳、复活币、事件、每日任务等重置 | 当前项目仍以 `DailyResetService` 读写时结算为权威, timer 只做在线通知/缓存刷新 |
| `Timer_StayTimeEvent` / `Timer_Send_Ontime_Reward` | `0x863b928` / `0x863aa90` | 在线停留和在线奖励存在 timer 巡检/批处理 | 奖励必须落库或有领取校验; timer 不直接凭空发奖 |
| `TimerStamina::dispatch_sig` | `0x8633cbc` | 在线 stamina 自然恢复每 60 秒 tick, 校验 `LoginTick` 和角色号 | 在线 UI 刷新可接入; 离线恢复必须持久化截止时间 |
| `TimerCardSelect::dispatch_sig` | `0x86343ba` | 翻牌自动流程用队伍 timer key 校验后推进 | 当前翻牌/副本短 timer 可迁移, 但必须校验当前 run/phase/version |
| `Timer_DungeonInoutOpenTime` / `CloseTime` | `0x8639e0a` / `0x863a1d0` | 副本/活动开闭用 timer 刷新内存标记并广播 | 入口请求仍要按当前时间和配置最终校验 |
| `TimerEPLPReturnVillage` | `0x8634c06` | 短时返村 timer 用 party key 校验后执行 | PR #139 死亡回城可按同类模型设计, 但不是旧服死亡 10 秒同名证据 |

## 接入判定表

| 类型 | 是否使用 `ClockService` | 依据和要求 |
| --- | --- | --- |
| 团本 ready 3 秒、攻坚 2400 秒、阶段切换 | 是 | 属于在线短时控制。回调必须校验团本对象、阶段、版本号。 |
| 副本翻牌 2 秒/4 秒、EPLP 返村、死亡等待回城 | 可以 | 旧服有翻牌、失败、返村 timer key 模型。当前项目接入前先保存当前 run/death sequence, 到期后重查。 |
| 心跳包、连接检查、服务间 heartbeat | 是 | 旧服 `TimerCheckConn` / `Timer_HadesHeartBeat` 有依据。必须校验 session/version, 断线要清理。 |
| 在线挂机、在线停留、在线奖励提醒 | 可以 | timer 只做在线巡检、提醒或投递已有待处理任务。发奖必须走领取校验或落库记账。 |
| 虚弱/stamina 在线恢复 UI | 可以 | 在线 tick 可用; 离线恢复必须用 `recover_end_unix` 或等价持久化状态在登录/查看时结算。 |
| 疲劳、副本次数、活动每日/每周重置 | 不作为权威 | `DailyResetService` / 读写时结算是权威。timer 只负责在线广播、缓存刷新、兼容旧协议瞬时通知。 |
| 租赁、会员、拍卖、道具期限、头像/宠物期限 | 不作为权威 | 保存绝对到期时间, 登录/使用时读库判定。timer 只做在线提醒或当前会话清理。 |
| 日志、统计、在线时长批处理 | 可以 | 可用固定时刻 timer 触发统计落库, 但不能影响玩家状态正确性。 |

## 当前已接入

### 副本通关翻牌自动流程

- 位置: `Server/DfoServer/Game/Dungeon/CardRewardService.cs`、`Server/DfoServer/Game/Dungeon/DungeonRun.cs`、`Server/DfoServer/Network/Handlers/Dungeon/DungeonRunLifecycle.cs`。
- 行为: 通关结算后注册 `dungeon-card:{sessionId}:auto` 一次性 timer; 2 秒后发送翻牌布局, 再注册 4 秒后自动翻免费卡。
- 依据: 旧服 `TimerCardSelect::dispatch_sig` 会取 party 并校验 `check_timer_key(16, key)` 后才推进翻牌; 当前项目用 `AutoFlipTimerVersion` 表达同类 key 失效语义。
- 约束: 回调只推进当前在线副本局的翻牌 UI/发包流程。若玩家手动点牌、返城、断线、换局, `DungeonRunLifecycle.CancelAutoFlip` 会先推进版本号并取消句柄, 已经出队的旧回调也会因版本不匹配而失效。
- 不做: 不把奖励正确性放在 timer 上。真正发奖仍走翻牌业务路径, 并由 `FreeCardRewardDelivered` / `PaidCardRewardDelivered` 保证免费/付费两段各自只入库一次; timer 只是触发自动选择免费卡。

### 在线宠物饱食度

- 宠物分钟回调在 Clock 调用线程内串行完成持久状态结算，不使用脱离调度顺序的 `Task.Run`，确保它结束后才执行全局背包自动保存。
- 事务前先在当前 owned `InventoryLease` 锁内预览饱食度变化；没有可见数值变化、没有装备宠物或宠物已满时不得打开 SQLite 连接。
- 宠物复活只在真正进入城镇的生命周期入口执行，分钟巡检不重复发起复活事务。
- 零延迟死亡检查提交失败时按固定正延迟重试，并继续校验 session、run identity 和 timer version；禁止失败后立即形成无退避重试环。

## 重启和时间回拨

`ClockService` 不会补放进程停机期间错过的回调。墙钟被拨回时, 周期 timer 会重新定位, 当前轮不触发。业务回调必须幂等, 并在执行前校验当前状态。

对于需要持久化的团本进度, 建议落库保存阶段和阶段截止时间:

1. 读取团本阶段和阶段截止时间。
2. 如果阶段已经过期, 直接按持久化规则结算或修正状态。
3. 如果阶段仍有效, 用剩余时间重新注册一次性 timer。

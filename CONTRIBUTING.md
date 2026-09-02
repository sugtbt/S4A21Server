# 贡献指南

欢迎参与项目。开始修改前必须先阅读 `AGENTS.md` 和 `Docs/服务端业务开发规范.md`，再按本文件执行。相关功能系统的完整说明放在 `Docs/`，需要时再查。

## 先做这几件事

1. 先把分支同步到最新 `main`（建议使用 rebase）。新版背包已经合入，基于旧 `SqliteInventoryStore` 的代码不能直接提交。先检查工作区已有改动，不得覆盖或回滚他人修改。
2. 构建服务端：

   ```powershell
   dotnet build Server/DfoServer/DfoServer.csproj -c Debug --nologo
   ```

3. 运行和改动有关的自测。可用参数见 `Server/DfoServer/Program.cs` 的 `SelfTestRegistry`。

   ```powershell
   dotnet run --project Server/DfoServer/DfoServer.csproj -c Debug --no-build -- --selftest-<name>
   ```

   新功能和问题修复要补对应自测；改到公共奖励、物品移动、保存、刷新或封包代码时，再跑 `--selftest-all`。如果主线本来就有失败，分别记录主线和当前分支的结果，不要新增失败。

4. 一个 MR 只解决一个问题，不要顺手混入无关重构、格式化或生成文件。
5. MR 描述写清楚修改批次、业务所有者、事务边界、数据库/PVF/协议影响和实际跑过的测试。没有自动化覆盖的功能，要说明实机验证了哪些场景。
6. 规则变化时改写对应开发规范正文。不得在规范中写入更新记录；修改说明写在 MR/提交描述中。

## 改背包时别绕路

碰到背包、仓库、装备、时装、宠物、物品奖励、商城或金币，先看：

- `Docs/新版背包架构业务接入规范.md`
- `Docs/GM工具_新背包表结构与ItemCore语义.md`（改数据库或 GM 工具时）

按业务域还必须联查：

- Dungeon、任务副本、特殊机制、塔类、Tournament、Blood Altar：`Docs/副本架构业务接入规范.md`
- 副职业、分解、附魔、炼金、控偶：`Docs/副职业架构业务接入规范.md`
- timer、在线恢复、周期任务：`Docs/ClockService.md`

如果只记五件事，请记这些：

1. **在线背包只认 `InventoryService`。** 先取得属于当前连接的背包租约 `InventoryLease`，再在 `lease.SyncRoot` 内完成读取、判断和修改。拿不到租约就返回失败，不要偷偷改数据库兜底。
2. **先找现成服务。** 发物品走 `InventoryRewardGrantService`，删除走 `InventoryDeleteService`，移动或穿戴走 `InventoryMoveService`，其他功能先找同类业务服务。协议处理器不要直接调用 `SetItem/RemoveItem` 或底层插入接口；业务服务完成规则校验后可以按需使用。
3. **金币和其他虚拟数量不是普通物品。** 在线金币、复活币、胜点和晶体都由 `InventoryService` 管理，不要再造第二套在线状态，也不要给它们手工创建普通 `ItemCore`。
4. **先改在线背包，再保存。** 普通变化会标记为待保存，由统一生命周期落库；只有必须在成功回包前落库的功能，才需要调用 `InventoryPersistenceService.SaveDirty(lease)`。不要先写 SQL，再想办法把结果塞回在线背包。
5. **刷新走现成入口。** 需要额外刷新槽位时，使用 `InventoryRefreshSender` 的 0x0D/0x0E 方法；如果成功回包已经包含完整变化，则按客户端实际处理方式决定是否省略。金币、外观和装备锁也都有对应方法。不要在协议处理器里手拼完整物品数据，也不要把 `ItemCore.ToBytes()` 直接当成封包内容。

不要在 `lease.SyncRoot` 里 `await`、发包或做长时间 I/O。

## 几条不能踩的线

- 普通在线协议处理器和业务服务不得直接写新背包表或附加数据表。
- 当前数据库基线是 `86jp-database-v1 / schema v1`，发布包不携带种子库，数据库不存在时由代码根据 `Server/DfoServer/Sqlite/item_schema.sql` 创建。
- 只有新增功能确实需要结构变化时，才从新体系 schema v2 开始追加连续迁移；必须同时更新 `item_schema.sql`、迁移注册、数据库自测和文档，已发布迁移不得修改、重排或复用。
- 多张表组成的一次业务写入必须放在同一个事务里。
- 扣费失败就返回失败回包，不能吞掉错误后继续发奖励。
- 客户端会用相同种子复算的掉落使用房间的 `DnfLcg`；其他服务端随机使用 `Infrastructure/ServerRandom`，不要自行 `new Random()`。
- 周期任务注册到 `Infrastructure/ClockService`，不要自己开线程写 `while` 循环。停机期间会错过的结算，还要能在下次读取或写入时补算。
- 改到共享代码时，原来经过这条路径的旧功能也要复测。只证明新功能能跑，不足以说明共享改动可以合入。

旧表和兼容代码只用于迁移、诊断和一次性修复。不要因为看到现有例外，就把新的在线业务接回旧 Store/DTO 或数据库直写路径。

## 协议改动要拿证据说话

86JP 客户端是协议字段的最终依据。动包格式前先查已有逆向结论和实现，MR 中标明证据来源：

- 86JP IDA 数据流和地址
- 抓包样本及十六进制包体
- PVF 文件路径和字段
- 仅作参考的旧服务端或其他客户端版本

不要靠名称相似、字段对齐或相邻调用猜语义。还没坐实的结论就明确写“待确认”。

自动化覆盖不了的客户端交互，要实机验证请求、回包、刷新和重登后的保存结果。拿不准实现方式时，先找同类业务，不要另起一套抽象。

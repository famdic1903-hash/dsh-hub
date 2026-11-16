# 修改说明（z77 修改版）

- 基础版本：`dsh-wechat-plugin` **0.47.0**（2026-10-26 发布）
- 原项目： <https://github.com/xcisxc29/dsh-wechat>
- 原许可证：MIT（见本目录 `LICENSE`，版权归 dsh-wechat contributors）
- 修改者：z77
- 本修改版同样以 MIT 发布（见 `LICENSE.z77`）

## v4（0.47.0-z77.5）：按 turn 粒度重做来源分流

原版不区分来源；v8–v10 用 session 级单槽 `#wechatTurns` 猜来源，带工具调用的回合在第一个 attempt 结束时标记被删，最终回答被误判为桌面来源而丢弃。v4 改为：

1. **turn 粒度**：`Map<"sessionId#turn", TurnState>`，记录 `channels/lastChannel/status/activeAttempts/pendingApprovals`；
2. **来源判定**：订阅 `agent/inbox/claimed`，用 `message.source.rpcId` 是否以 `wechat-` 开头判定渠道，并绑定到该事件携带的 `turn`；
3. **attempt 关联**：`start` 帧建立 `attemptId → {turn,step}`；`chunk/end` 帧用 attemptId 找回 turn，不再依赖 session 单槽；
4. **工具/审批关联**：`tool-call-delta.id` 与 `tool/call.callId` 建立 `sessionId#callId → turn`；审批用 `request.callId` 精确命中，取不到时仅在该会话恰好一个非终结 turn 时兜底，否则交桌面；
5. **回复缓冲**：`#replies` 改为 `Map<"sessionId#turn", buffer>`，per-turn single-flight，flush 期间新文字置 dirty 下一轮发送；
6. **发送失败**：文本进入 `#outbox` 按退避重试（最多 20 次），不因一次失败丢弃；
7. **steer 策略**：只有恰好一个同渠道（微信）非终结 turn 时才 steer，否则 queue；跨端插话不再合并进同一 turn；
8. **混合 turn**：回复和审批一律桌面兜底，不向微信转发；
9. **生命周期**：只有 `session/event` 的 `turn/end` 才把 turn 标为 ending；等 activeAttempts 清空、审批结清、缓冲/outbox 清空后才 purge；另有 30 分钟 TTL 与 2 小时审批超时兜底；
10. **dispose**：同步 disposer 中 `shutdown()` 快照待发送 buffer/outbox 并尽力发送，只承诺尽力、不保证送达。

未改动登录、协议、设置页、媒体处理等其余逻辑。


## v4.1（0.47.0-z77.6）：按审查意见修订

- 只在 `turn/end` 确认来源后发送；attempt end 不再触发发送，混合 turn 整段丢弃；
- `#sendTurnReply` 全量异常保护，只有 `#reply === true` 才算成功；
- `#flushOutbox` 单飞 + 批次隔离，失败保留批次；超过 20 次或超过 20000 字符写入 `failed-replies` 并记录错误；
- shutdown 跳过在途批次，dispose 后不再创建重试任务；
- steer 仅限"恰好一个纯微信非终结 turn"，其余 queue；
- 审批使用独立 AbortController（5 分钟超时）、settle-once、终态保留 10 分钟，pendingApprovals 归零才恢复 running；
- rpcSessions 10 分钟 TTL，stale attempt 60 分钟清理。


## v4.2（0.47.0-z77.7）：第二轮审查修订

- 文本帧改为同步累加 `#pendingText`，`turn/end → #finalizeTurn` 统一发送，消除异步竞态与乱序；
- `#flushReply` / `#flushOutbox` 循环消费发送期间新增文本，结束后补排，不留无任务文本；
- shutdown 区分在途/未发送，保留在途条目并续发剩余文本；disposed 后不再创建定时器/outbox；
- 审批 5 分钟超时用 AbortController + Promise.race 双保险，异常与迟到回答均处理；
- 失败文本文件名加 pid+uuid，优先写 `failed-replies`，失败退到 `%TEMP%`，再失败记录错误；
- attemptId 使用 `sessionId#attemptId` 复合键；stale attempt 60 分钟清理；rpcSessions 10 分钟 TTL。


## v4.3（0.47.0-z77.8）：第三轮审查修订

- finalize 纳入 `#finalizing` 在途管理；store.read 之后重新检查 disposed，disposed 后不创建 reply/outbox；
- ending 状态到达的文本不再丢弃：追加 `#pendingText` 并重排 finalize（setTimeout 0），`#runFinalize` 单飞消费；
- shutdown 跟踪在途 finalize/flush/outbox，保留在途批次引用并续发剩余文本；同步 disposer 只保证尽力发送；
- 审批/提问使用统一 5 分钟 deadline（`#withBudget` 覆盖 `#askTarget + #ask`），超时 abort、清理定时器与 listener；
- outbox 成功后清理空 reply entry；flushReply/flushOutbox 异常均恢复批次到 outbox 或落盘；
- `#reply` 返回值已核实：`#sendTurnReply` 只认 `=== true`。


## v4.4（0.47.0-z77.9）：第四轮审查修订

- finalize 结束条件明确为 turn/end(endedAt) + activeAttempts 空 + 300ms 收尾窗口；迟到帧进 pending 并重排，不丢不误判；
- purge 需 finalizedAt 超过 1s 且无 pending/reply/outbox/finalizing；
- flushReply 用 entry.inFlightText 保存当前批次，发送/入 outbox/落盘任一步失败都可恢复；
- 审批 deadline 从 listener 入口计算，conversation 查询、askTarget、ask 全部受 5 分钟约束；
- 落盘改为 .tmp + renameSync 原子替换；目录不可写时进内存 #emergencyHold，维护任务重试；
- shutdown 按批次所有权处理在途/未发送文本，disposed 后不再创建定时器。


## v4.5（0.47.0-z77.10）：第五轮定点修订

- flushReply 的 disposed 分支改为 if/else 互斥，失败批次只交给一个后续处理者；
- outbox overflow 时若正在发送，不删除 Map，超限新增文本单独落盘，保留在途批次所有权；
- flushOutbox 增加 inFlightText，异常路径用它恢复/落盘当前批次；
- 落盘依次尝试 home/failed-replies、home/wechat/failed-replies、%TEMP%；全失败进内存 emergencyHold，日志明确 memory-only 不保证持久化；
- #withinDeadline 同时竞争 abort，外部取消立即结束等待；迟到结果不改变已结算审批；
- turn/end 后若 activeAttempts 非空，2 秒强制 finalize 并清理未观测 attempt（附 DSH 源码 settle/abandon 证据）。

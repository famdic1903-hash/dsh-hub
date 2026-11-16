# 更新日志（CHANGELOG）

版本规则：
- **仓库**用语义化版本 `vMAJOR.MINOR.PATCH`，每次发布打 tag 并建 GitHub Release；
- **插件**各自保留自己的版本号：原创插件按自身功能演进；修改版沿用上游基础版本并加 `-z77.N` 后缀；
- 破坏性改动（安装方式/协议变更）升 MAJOR，新增功能升 MINOR，修复升 PATCH。

## [v1.0.1] - 2026-11-16

微信插件升级到 **`0.47.0-z77.10`**，把 main 上 v8–v10 的来源分流与 v4.1–v4.5 的修复合并发布。

### 修复

- **带工具调用/多 step 回合最终答复丢失**：不再把 attempt end 当作 turn end，按 turn 缓冲，`turn/end` 后统一发送；
- **桌面/混合回合回复泄漏微信**：只有纯微信回合才发微信；混合 turn 的回复与审批桌面兜底；
- **审批路由错误**：用 callId→turn 精确归属；纯微信回微信，桌面/混合回桌面；统一 5 分钟超时，settle-once；
- **插话/排队串线**：仅“恰好一个纯微信非终结 turn”允许 steer，其余 queue；
- **发送失败丢文本**：outbox 退避重试、失败落盘 `failed-replies`、overflow 在途批次保护、内存 emergencyHold；
- **缺少 message_id 被误判失败导致重复发送**：以“服务端已接受”为成功，后处理日志/记账隔离；
- **dispose/重启丢待发送文本**：同步 disposer 快照尽力发送（不保证进程立即退出时送达）；
- **finalize 竞态/迟到文本**：收尾窗口、pending/orphan 落盘、未观测 attempt 兜底 finalize。

### 结构

- 删除 session 单槽 `#wechatTurns` / `#activeTurn`；
- 改为 `sessionId + turn` 粒度：`#turns`、`#attempts`、`#calls`、`#replies`、`#outbox`、`#approvals`；
- 来源由 `agent/inbox/claimed` 的 `source.rpcId` 判定。

### 说明

- `v1.0.0` tag 仍指向旧版 `0.47.0-z77.2`；
- 本版为 `v1.0.1`，插件版本 `0.47.0-z77.10`；
- 未改动登录、协议、设置页、媒体处理等其余逻辑。

## [v1.0.0] - 2026-11-15

首个公开版本。

### 包含插件

- `plugins/wechat-origin-routing/` — 微信渠道 z77 修改版
  - 基础：`dsh-wechat-plugin` 0.47.0（上游 MIT）
  - 本版：**0.47.0-z77.2**
  - 修改：按回合来源分流（微信发起的回合才推送微信/在微信审批；桌面发起的回合只留在桌面）；修复带工具调用的长回合回复丢失
- `plugins/whale-pet/` — 蓝色大肥鱼桌面鲸鱼（原创）
  - 本版：`1.1.0`

### 说明

- 两插件均以 MIT 发布，署名 z77；微信插件保留上游 LICENSE 与 `MODIFICATIONS.md`；
- 仓库封面由 AI 生成（Flux），已去除原始服务水印；
- 仓库已清理不含任何个人数据（截图、余额、本机路径、项目台账等）。

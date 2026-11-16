# z77 的 DSH 插件合集

把 DeepSeek Harness（DSH）接到微信和桌面：人在外面用手机发消息，电脑上的 DSH 干活。

> 署名：**z77 修改版 / 原创版**。每个插件目录里都有 `LICENSE` 与修改说明。

## 这是什么

- **微信渠道插件**：手机微信 ↔ 电脑 DSH。微信消息变成 DSH 任务，DSH 的最终答复回到微信；支持文件、图片、语音、视频，支持在手机上审批工具调用和回答选择题。
- **桌面鲸鱼**：把 DSH 的余额小鲸鱼挂到 Windows 桌面（原创，和微信插件互不依赖）。

不需要 OpenClaw，不需要公网 IP，不需要把电脑暴露到外网。电脑保持开机，手机扫码绑定即可。

## 插件列表

| 目录 | 插件 | 说明 |
|---|---|---|
| `plugins/wechat-origin-routing/` | **微信渠道（z77 修改版）** | 原版 `dsh-wechat-plugin` 的 turn 粒度来源隔离版，当前 **0.47.0-z77.10** |
| `plugins/whale-pet/` | **蓝色大肥鱼跳出窗口**（原创） | 透明置顶小窗，可拖动，单击弹余额气泡、右键菜单；原生 Win32，附源码 |

## 微信插件：原版是什么

- 原项目：**[xcisxc29/dsh-wechat](https://github.com/xcisxc29/dsh-wechat)**（MIT）
- 包名：`dsh-wechat-plugin`
- 本修改版基于：**0.47.0**
- 原版能力（本修改版保留）：
  - **双向文本**：微信消息 → DSH prompt，回答 → 微信
  - **图片、文件、语音、视频双向**：附件保存到会话工作区，agent 可用 `send_to_wechat` 发回
  - **一个微信对话管理多个 DSH session**：`/new`、`/list`、`/switch`、`/model`、`/current`、`/cancel`
  - **手机审批/选择题**：权限提示与多选题发到微信，回复编号即可
  - **设置页**：Settings → WeChat（权限预设、主动推送、附件合并窗口、回复长度、命令说明、日志）
  - **主动推送**：某个 session 可主动向微信发消息
  - **跨 workspace**：不只微信绑定的 workspace，其他 workspace 的 session 也可推送

## 微信插件：z77 改了什么

核心目标：**不把桌面任务的回复泄漏到微信，也不让微信回合的回复丢失**。

1. **按 turn 隔离来源**
   - 不再用 session 单槽猜来源；
   - 用 `sessionId + turn` 维护来源，`agent/inbox/claimed` 的 `rpcId` 判定渠道；
   - 微信发起的纯微信回合：回复/审批回微信；
   - 桌面或混合回合：回复/审批桌面兜底，不推微信。
2. **修复带工具调用/多 step 回合丢最终答复**
   - attempt end 不再当 turn end；
   - 按 turn 缓冲，`turn/end` 后统一发送。
3. **审批与选择题路由**
   - `callId → turn` 精确归属；
   - 纯微信回合的审批回微信，桌面/混合回桌面；
   - 5 分钟统一超时、settle-once、迟到回答不生效。
4. **插话/排队不串线**
   - 只有“恰好一个纯微信非终结 turn”才允许 steer，其余 queue；
   - 桌面消息插入微信 turn、微信消息插入桌面 turn 都走队列/混合 turn 的既定策略。
5. **发送可靠性**
   - 发送失败进入 outbox 退避重试；
   - 失败文本落盘 `failed-replies`；
   - overflow 时保留在途批次；
   - 缺少 `message_id` 不再被误判为失败导致重复发送。
6. **生命周期与退出**
   - turn/end + attempt 清空 + 缓冲清空后才回收；
   - dispose 时快照待发送文本，尽力发送（不保证进程立即退出时送达）。

详细修改见 `plugins/wechat-origin-routing/MODIFICATIONS.md` 与 `CHANGELOG.md`。

## 安装（本机已有 DSH 桌面端）

1. 克隆仓库：
   ```bash
   git clone https://github.com/famdic1903-hash/dsh-hub.git
   cd dsh-hub
   git checkout v1.0.1
   ```
2. 在 DSH 里安装对应插件目录（绝对路径）：
   ```text
   plugin_manager install_bundle → target: <仓库路径>/plugins/<插件目录>
   ```
   或命令行：
   ```bash
   dsh plugin --profile desktop add <仓库路径>/plugins/<插件目录> -w
   ```
3. 安装后完全退出并重新打开 DSH。
4. 微信插件：Settings → WeChat → 重新扫码绑定。

也可以直接从 Release 下载：
- v1.0.1：<https://github.com/famdic1903-hash/dsh-hub/releases/tag/v1.0.1>
- 插件 zip：`wechat-origin-routing-0.47.0-z77.10.zip`

> 微信插件是第三方项目的修改版，使用前请阅读其 `MODIFICATIONS.md` 与上游 `LICENSE`。

## 版本

- 仓库版本：语义化 `vMAJOR.MINOR.PATCH`，见 [CHANGELOG.md](CHANGELOG.md) 与 Releases；
- 微信插件：`0.47.0-z77.10`（基于上游 0.47.0）；
- 当前 Release：`v1.0.1`；
- 安装固定版本：建议 `git checkout v1.0.1` 后再安装，避免主线改动影响稳定性。

## 许可证

- 本仓库中由 z77 编写/修改的部分：MIT（见根目录 `LICENSE`）。
- `plugins/wechat-origin-routing/` 来自 MIT 协议的 `dsh-wechat-plugin`，原版权归 dsh-wechat contributors，原 `LICENSE` 保留在该目录；修改部分版权归 z77。
- 各插件目录内如有独立 `LICENSE`，以该目录为准。

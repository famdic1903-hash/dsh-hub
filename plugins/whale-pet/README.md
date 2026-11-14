# 蓝色大肥鱼跳出窗口（@local/dsh-whale-pet）

把 DSH Web 界面里那个「余额小鲸鱼」挂件（`dsh-whale-widget`）**放到 DSH 窗口外面**：
在桌面上开一个独立的透明置顶小窗显示同一条鲸鱼，可以拖到任意显示器（含副屏/负坐标），
DSH 窗口内留一个切换按钮，跳出后窗口里的鲸鱼自动隐藏，随时右键收回。

网页里的元素画不出浏览器/Electron 窗口边界，所以本插件由三部分组成：

| 文件 | 角色 |
|---|---|
| `index.js` | Host 半：模式状态、7 条本机 HTTP 路由、桌面宠物进程启停、几何持久化、气泡文本（余额 / 今日已用 / 峰谷时段） |
| `peak.js` | 峰谷判定（北京时间工作日 9–12、14–18 高峰；周末与法定节假日全天谷价），与窗口内挂件同一套规则 |
| `client.js` | Client 半：会话输入框下方的「🐳 鲸鱼跳到桌面 / 鲸鱼回到窗口」按钮；跳出时隐藏窗口内鲸鱼 |
| `pet/WhalePet.exe` | 原生 Win32 分层窗口（C# 源码 `pet/WhalePet.cs`，用系统内置 csc 编译）：透明、置顶、不抢焦点、点击穿透、可拖动、右键菜单、心跳自清理 |

## 安装

在 DSH 里用插件管理器安装本目录（绝对路径）：

```
plugin_manager install_bundle -> target: <本仓库>/plugins/whale-pet
```

装好后**完全退出并重启 DSH**（插件 Host 半在重启后加载；Client 半一般在安装当下就会热加载）。

## 使用

1. 打开任意会话，输入框下方会出现「🐳 鲸鱼跳到桌面」按钮。
2. 点它：DSH 窗口里的鲸鱼消失，桌面上出现同一条鲸鱼（默认在主屏右下角）。
3. 左键拖动到任意位置（可拖到竖副屏）；位置/大小会在下次启动时恢复。
4. **左键单击**（不拖动）= 戳一下：挤压弹跳 + 按压/松开音效 + 头顶弹出余额气泡（余额 / 今日已用 / 峰谷时段，5 秒后自动消失；显示中再点先收起）。气泡里的余额数字由页面每 60 秒从挂件自己的余额接口取一次并推给 Host；峰谷状态优先用挂件服务端值，取不到时按 `peak.js` 的同源本地规则判定，和窗口内挂件一致。
5. **右键**弹出菜单：
   - 「回到 DSH 窗口」/「关闭桌面宠物」——收回，窗口内鲸鱼恢复（带上原来的余额气泡/菜单）；
   - 「大小」——小 / 中 / 大 / 特大；
   - 「复位到右下角」。
6. 也可以再点输入框下方的「🐳 鲸鱼回到窗口」。

## 行为与数据

- 状态目录：`<DSH_HOME>\.whale-pet\`
  - `state.json`：`mode`（inside/outside）+ 浏览器上报的 `dpr`（诊断用）
  - `pos.json`：宠物几何（物理像素 x/y/size），由 Host 写入，用于下次启动恢复
  - `balance.json`：页面推送的最近一次快照（余额 / 今日已用 / 峰谷字段 / 法定节假日清单，气泡数据源）
  - `run.json`：每次启动桌宠时由 Host 生成的运行配置（排查用）
- 音效复用挂件自带预设（按 `.dshw-size.json` 的 `soundSet`：duck→Ya1/Ya2.mp3，fx1→D1/D2.mp3），音量取 `vol`；桌宠用 Windows MCI 播放（mp3）。
- 跳出时窗口内鲸鱼用 CSS 类 `html.dsh-whale-pet-outside` 隐藏（选择器 `.dshwv-root`），收回时自动移除。
- 桌宠进程与 Host 之间有 3 秒心跳；DSH 完全退出后桌宠约 12 秒内自行退出。
- 所有 HTTP 接口只接受 `127.0.0.1` 来源：

| 方法 | 路径 | 用途 |
|---|---|---|
| GET | `/api/whale-pet/state` | 当前模式 / 是否在跑 / 几何 |
| POST | `/api/whale-pet/toggle` | `{"mode":"outside"}` 或 `{"mode":"inside"}`，缺省为切换 |
| POST | `/api/whale-pet/recall` | 收回（桌宠右键菜单调用） |
| POST | `/api/whale-pet/pos` | 桌宠上报几何 `{"x":..,"y":..,"size":..}`（物理像素） |
| GET | `/api/whale-pet/poke` | 桌宠单击时取气泡文本（余额 / 今日已用 / 峰谷）+ 音效路径 + 音量 |
| POST | `/api/whale-pet/balance` | 页面推送实时余额快照 |
| GET | `/api/whale-pet/ping` | 桌宠心跳 |

## 可选配置

在 profile 的 `cordis.patch.yml` 里给 `whale-pet` 行加 config（都可省略）：

```yaml
- id: whale-pet
  config:
    imagePath: '<DSH_HOME>/profiles/desktop/node_modules/dsh-whale-widget/assets\DSniang1.png'
    size: 300   # DIP 边长，桌宠会按系统缩放换算成物理像素
```

## 回滚

```
plugin_manager remove_bundle -> "@local/dsh-whale-pet"
```

然后重启 DSH；如需彻底清干净，删除 `<DSH_HOME>\.whale-pet\`。
也可以在插件列表里只 **禁用** 该 bundle（保留安装）。

## 排错

- **点了按钮没反应 / 没出鲸鱼**：先看 `http://127.0.0.1:<端口>/api/whale-pet/state` 的 `error` 字段（端口见 DSH 地址栏）：
  - `桌面宠物程序不存在` → `pet/WhalePet.exe` 丢了，跑一次 `pet/build.ps1` 重新编译；
  - `找不到鲸鱼角色图` → 余额小鲸鱼挂件未安装，或给 `whale-pet` 行配 `imagePath`。
- **桌宠出来了但没记忆位置**：位置/大小由 Host 写 `<DSH_HOME>\.whale-pet\pos.json`；如果这个目录不可写，会退化成每次默认右下角。
- **桌宠没有日志**：从 DSH 里拉起时，DSH 沙箱令牌（Low 完整性 + write-restricted token）会拒绝桌宠写 `<DSH_HOME>\.whale-pet\`；桌宠会依次尝试 `.whale-pet` → `%LOCALAPPDATA%\WhalePet` → `%TEMP%\WhalePet` → exe 目录，都没有就无日志。这不影响功能（几何走 Host 中转）。
- **鼠标在鲸鱼上显示转圈圈**：v1.0.0 的已知问题，v1.0.1 已修（窗口类光标固定为 IDC_ARROW）。还看到说明跑的是旧 exe——收回再点「鲸鱼跳到桌面」会拉起新的。
- **点一下没声音**：看桌宠日志里的 `mci open ok ... rc=0`；出现 `rc=266` 说明跑的是 v1.1.0 之前的旧 exe（MCI 需要 STA，1.1.1 已修）。音效跟随挂件设置：挂件里关掉音效（`.dshw-size.json` 的 `sound:false`）或音量调 0 时桌宠也静音。
- **气泡只显示「余额 —」**：Host 还没拿到余额快照（页面每 60 秒推一次，刚重启 DSH 后要等一轮），或挂件账本 `$DSH_HOME\.dshw-usage.json` 不可读；等一分钟再点。看 `<DSH_HOME>\.whale-pet\balance.json` 是否出现 `"source": "page"`。
- **怀疑有残留进程**：任务管理器里看看有没有 `WhalePet.exe`。桌宠已取消单实例限制，多开不会互相顶掉；残留实例在 DSH 退出约 12 秒后自己关掉。

## 已知限制（v1）

- 桌面上的是「纯宠物」：显示角色图，单击可看气泡（余额 / 今日已用 / 峰谷）并有点按动画与音效；原来的**气泡菜单、余额面板、挂件设置菜单**仍只在窗口内版本可用（收回即可）。
- 混合 DPI 屏（本机主屏 200% + 副屏 100%）下按主屏系统缩放换算；拖到 100% 副屏时由 Windows 做缩放映射，视觉尺寸一致但可能略软。
- 桌宠不显示在任务栏/Alt-Tab；全屏独占应用之上不保证可见。
- 角色图默认取余额小鲸鱼挂件自带 `DSniang1.png`；自定义角色（`whale-roles/`）暂不跟随。

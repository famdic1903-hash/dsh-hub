# WhalePet —— 可跳出 DSH 窗口的桌面宠物（Windows 原生窗口）

`WhalePet.exe` 是一个独立的小窗口进程：透明、置顶、无边框、不抢焦点、不在任务栏/Alt-Tab 显示，
可以拖到整个桌面（包括 x 为负的竖屏副屏），支持右键原生菜单、位置/大小记忆和心跳自动退出。
**左键单击（v1.1.0 起）= 「戳一下」**：挤压/弹跳动画 + 按压/松开音效 + 头顶余额气泡（气泡内容由 Host 提供）。
音效在**独立 STA COM 线程**播放（v1.1.1 修复：mpegvideo/DirectShow 驱动要求 STA，否则 `mci open` 返回 rc=266）。

DSH 侧两条命令即可：

1. 需要宠物时 `WhalePet.exe "<config.json 绝对路径>"` 启动；
2. 用户恢复时直接杀掉该进程（宠物也会在心跳连续 4 次失败后自行退出，exit code 0）。

## 文件

| 文件 | 说明 |
|---|---|
| `WhalePet.cs` | 全部实现（单文件，C# 5，仅 .NET Framework 内置程序集，UTF-8 带 BOM） |
| `WhalePet.exe` | 编译产物（`/target:winexe`，anycpu） |
| `build.ps1` | 编译脚本（运行中的 exe 被占用时会自动换名替换，不会失败） |
| `test.ps1` | 自动化验收脚本（截图 / 心跳 / 菜单 / 拖拽 / DPI / pos 上报 / 单击气泡与音效） |
| `README.md` | 本文件 |

## 编译

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ /platform:anycpu /out:WhalePet.exe /r:System.Drawing.dll WhalePet.cs
```

或 `powershell -ExecutionPolicy Bypass -File build.ps1`。

注意：`WhalePet.cs` 必须保存为 **UTF-8 带 BOM**（旧版 csc.exe 无 BOM 时按系统 ANSI 代码页读源码，中文菜单会乱码）。
当前文件已带 BOM。

## 配置（run.json）

```json
{
  "imagePath": "K:\\dsh-home\\profiles\\desktop\\node_modules\\dsh-whale-widget\\assets\\DSniang1.png",
  "posPath": "K:\\dsh-home\\.whale-pet\\pos.json",
  "apiBase": "http://127.0.0.1:59391",
  "size": 300,
  "x": 1500,
  "y": 620,
  "idleBob": true,
  "physical": { "x": 3000, "y": 1240, "size": 600 }
}
```

| 字段 | 说明 |
|---|---|
| `imagePath` | 必填，角色 PNG（任意尺寸，按比例缩放居中，透明保留） |
| `posPath` | 必填，位置记忆文件路径；目录不存在会自动创建。**只作为兜底缓存**，见下 |
| `apiBase` | 必填，DSH 接口根地址（末尾 `/` 自动去掉） |
| `size` | **DIP**（与 Electron/CSS 像素同一量纲）。实际窗口边长 = `round(size × effectiveDpr)` |
| `x` / `y` | **DIP** 初始左上角。实际物理坐标 = `round(value × effectiveDpr)`；可缺省 |
| `idleBob` | 静止时上下浮动 ±3 物理 px、周期约 3s；拖拽/菜单时不浮动；持久化基准位置 |
| `dpr` | 可选。缺省时**自动探测**：`max(1, round(GetDpiForSystem()/96))`（本机主屏 192dpi → 2）；显式给出则覆盖 |
| `physical` | 可选。Host 上一轮从 `POST /api/whale-pet/pos` 收到的值，**物理像素，原样使用，不再乘 dpr**。优先于其它位置来源 |

启动几何优先级：

1. `config.physical` 的 `{x, y}`（size 若存在也用它，`ClampSize` 限幅 48–1024，不再乘 dpr）；
2. 现有 `pos.json`（物理像素，不乘 dpr，兼容旧行为）；
3. `config` 的 `x`/`y`（DIP，乘 effectiveDpr）；
4. 主屏工作区右下角留 24 物理 px。

尺寸优先级：`physical.size` → `pos.json` 的 size → `config.size × effectiveDpr` → 230。
`pos.json` 格式：`{"x":3000,"y":1240,"size":600}`（物理像素）。

## HTTP 约定

- 心跳：每 3000ms `GET {apiBase}/api/whale-pet/ping`，超时 1500ms；**连续 4 次失败 → exit code 0**。
- 收回：菜单「回到 DSH 窗口」/「关闭桌面宠物」→ 后台线程 `POST {apiBase}/api/whale-pet/recall`
  （空 body），成功或失败都 exit code 0。
- **戳一下（v1.1.0 新增）**：左键单击 → 后台线程 `GET {apiBase}/api/whale-pet/poke`（超时 1500ms），
  期望返回：
  `{"ok":true,"lines":["余额 ¥555.67","今日已用 ¥1.42"],"pressSound":"<绝对路径>","releaseSound":"<绝对路径>","volume":0.9,"bubbleMs":5000}`。
  所有字段可缺省/为空：`lines` 为空 → 气泡显示「余额 —」；音效路径为空或文件不存在 → 跳过并写日志；
  请求失败/超时 → 仍然弹一下并显示「余额 —」。宠物**从不自己计算余额**，只显示 Host 给的行。
- **位置上报（新增）**：拖拽结束 / 改大小 / 复位后，后台线程
  `POST {apiBase}/api/whale-pet/pos`，`Content-Type: application/json`，
  body `{"x":整数,"y":整数,"size":整数}`，单位是宠物自己的坐标空间 = **物理像素**
  （即 `GetWindowRect` 的 Left/Top 与窗口边长）。超时 1500ms；失败只写日志，不影响下次启动、不退出、不弹窗。
  Host 负责落盘，下次启动通过 `config.physical` 传回。**pos.json 写盘只是尽力而为的兼容兜底。**
- 所有请求在后台线程执行，不阻塞窗口消息循环；异常只写日志，不弹 MessageBox。

## 窗口行为

- `WS_POPUP` + `WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST`；
  `ShowWindow(SW_SHOWNOACTIVATE)` 启动不抢焦点；`WM_MOUSEACTIVATE → MA_NOACTIVATE`。
- `UpdateLayeredWindow` + 预乘 alpha 的 32bpp BGRA DIB（`CreateDIBSection`，top-down）渲染，
  `HighQualityBicubic` 缩放、逐像素预乘。
- **光标**：窗口类 `wc.hCursor = LoadCursor(0, IDC_ARROW)`，WndProc 处理
  `WM_SETCURSOR → SetCursor(IDC_ARROW); return 1`，`ShowWindow` 后补一次 `SetCursor`。
  这样鼠标移入/停留在宠物上始终显示标准箭头，不会继承上一个窗口的光标状态
  （用户实测的「鼠标在上面转圈圈」就是继承了别的程序的 IDC_APPSTARTING 光标）。
- 左键拖动：`WM_LBUTTONDOWN` 记录客户端坐标偏移 + `SetCapture`；`WM_MOUSEMOVE` 用 lParam 客户端坐标
  做增量移动（`SWP_NOSIZE|SWP_NOACTIVATE|SWP_NOSENDCHANGING`，保持 TOPMOST）；`WM_LBUTTONUP` 释放并上报。
- **左键单击（v1.1.0 起）= 戳一下**：按下到抬起位移 < 4 px 且时长 < 400 ms 视为单击：
  1) 播 `pressSound`，约 120ms 后播 `releaseSound`。每个音效在**自己的 STA 线程**里播放：
     线程入口 `CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED)`（S_OK/S_FALSE 继续、
     `RPC_E_CHANGED_MODE` 记录后继续尝试、其它错误记录不崩），结束 `CoUninitialize()`；
     这样 mp3 的 `mpegvideo`（DirectShow）驱动才能加载（否则 `mci open` 返回 **rc=266
     MCIERR_CANNOT_LOAD_DRIVER**）。`setaudio volume to round(volume×1000)` 失败只记日志
     （有的设备不支持 MCI 音量），播放失败/缺文件也不影响动画与气泡；wav 额外保留
     `waveaudio` 与 `SoundPlayer` 兜底；
  2) 挤压/弹跳动画：5 帧 × 60ms（scaleY 1.0→0.91→1.05→…，scaleX 反向补偿，锚点为角色底部中心），
     动画不改变持久化基准位置；
  3) 角色头顶显示余额气泡（多行文本，`bubbleMs` 默认 5000ms 后自动消失；显示期间再次单击先立即
     收起旧气泡再重新戳）。
  气泡是**独立的透明置顶分层窗口**（类名 `WhalePetBubbleWindow`，`WS_EX_TRANSPARENT` +
  `WM_NCHITTEST` 永远 `HTTRANSPARENT`，完全点击穿透、不抢焦点）。它不改变宠物窗口的
  尺寸/几何/hit-test/持久化语义，所以拖拽、贴边、pos 上报、重启记忆与 v1.0.1 一致；气泡随
  拖拽/空闲浮动跟随角色。右键仍然是原生菜单。
- 透明穿透：`WM_NCHITTEST` 用 lParam 屏幕坐标 → `ScreenToClient` → 查 alpha 表，alpha < 16 返回
  `HTTRANSPARENT`，否则 `HTCLIENT`。
- 右键菜单：`CreatePopupMenu/AppendMenu/TrackPopupMenu`（`#32768` 系统菜单）。菜单项：
  回到 DSH 窗口 / 大小（小中大特大）/ 复位到右下角 / 关闭桌面宠物。
  **「大小」四档是 DIP 值**：170/230/300/380 DIP，实际 = `round(DIP × effectiveDpr)`，
  本机 200% 下为 340/460/600/760 物理 px；切换保持窗口中心不动。
- 菜单兜底：先 `SetForegroundWindow`（去 NOACTIVATE → AttachThreadInput → SwitchToThisWindow 兜底）；
  若因 UIPI/沙箱抢不到前台，由 80ms watchdog 在“菜单和宠物窗口外按下鼠标”或按 ESC 时 `EndMenu()`；
  同时处理 `WM_CANCELMODE`。
- **无单实例互斥**（v1.0.1 起）：每次启动都创建自己的窗口，绝不静默退出。Host 侧已有
  `isPetAlive` 判重；残留实例会在心跳失败约 12s 后自退。原因：旧版命名互斥体在“已有实例
  但不可见/在别的屏幕”时会让新实例静默 `exit 0`，用户看到的就是“点了没反应/点了没出现”。
- 托盘图标：无（按需求）。
- 日志：优先写 `posPath 所在目录\pet.log`；写不了时依次尝试
  `%LOCALAPPDATA%\WhalePet\pet.log` → `%TEMP%\WhalePet\pet.log` → exe 所在目录 `pet.log`
  （先真实打开探测可写性，成功后写一行说明用了哪个兜底路径）。
  若进程令牌被 DSH 文件沙箱限制到任何位置都写不了，则日志静默丢弃（不影响功能）。

## 测试

```
powershell -ExecutionPolicy Bypass -File test.ps1              # 全部（约 80s，失败自动重试最多 3 次）
powershell -ExecutionPolicy Bypass -File test.ps1 -Mode visual|menu|drag|bob|dpr|click|poke
powershell -ExecutionPolicy Bypass -File test.ps1 -Mode click -RealMouse   # 真实鼠标注入路径（仅限非沙箱进程）
```

测试进程先 `SetProcessDPIAware()`，用**物理像素**与 pet 对齐；截图/日志写
`<临时目录>\`。覆盖：

- 洋红置顶背景 + 截图：透明像素透出背景色、不透明像素画出角色（非黑块/白块）；
- 桩 HTTP 服务（127.0.0.1:59888）记录 ping / recall / **pos 上报的 body**；
- `PostMessage` 合成拖拽（含拖到 x 为负的副屏）、右键菜单、改大小、复位，不移动真实鼠标；
- 心跳：无服务约 13.7s 自动退出、exit code 0；
- **自动 dpr**：无 `dpr` 时 config size 300 DIP → 600 物理 px；显式 `dpr:3` 覆盖；
- **菜单 DIP**：小(170) → 340 物理 px 且中心保持；
- **pos 上报**：拖拽/改大小/复位后桩服务收到 `POST /api/whale-pet/pos`，body 的 x/y/size 与 `GetWindowRect` 一致；
- **config.physical**：原样使用（1200,800,400 不乘 dpr），优先于 config x/y/size；
- **无互斥**：第二次启动也会出现自己的 `WhalePetWindow`（不再静默退出）；
- **光标**：`GetClassLongPtr(hwnd, GCLP_HCURSOR)` == `LoadCursor(IDC_ARROW)`（非 0）；
  `SendMessage(WM_SETCURSOR)` 返回 1 且 pet.log 记录 `WM_SETCURSOR -> SetCursor(IDC_ARROW)`；
- **单击/戳一下（v1.1.0）**：左键单击 → 桩服务收到 `GET /api/whale-pet/poke`、**不弹菜单**；
  气泡窗口 `WhalePetBubbleWindow` 出现且可见、水平居中并位于角色头顶（`bubbleBottom <= petTop`）；
  `bubbleMs` 到点自动隐藏；显示期间再点先收起（日志 `bubble hidden (re-poke)`）；空 payload →
  气泡显示「余额 —」；音效缺文件 → 日志 `sound skipped (missing file)` 且进程不退出；
  截图 `poke-bubble.png`；
- **音效（v1.1.1）**：桩响应的 `pressSound`/`releaseSound` 指向余额小鲸鱼自带的真实
  `Ya1.mp3`/`Ya2.mp3`；断言 `mci open ok ... type=mpegvideo rc=0`、无 `mci open failed`、
  无 `rc=266`、日志有 `sound play`/`setaudio`；缺文件路径 → `sound skipped (missing file)`
  且进程不退出；
- **右键**：`WM_RBUTTONUP` → 弹出菜单（截图 `menu-open.png`）；`WM_CANCELMODE` 关闭；
- **拖动**：有位移的按下/移动/抬起 → 只拖动，不弹菜单、不戳；
  结束用 `WM_CANCELMODE`/`EndMenu` 关菜单。
  说明：默认走 `PostMessage` 路径（同一套 WndProc 代码）；`-RealMouse` 会先探测
  `SetCursorPos` 能否移动光标，可用时才走真实 `mouse_event` 点击/拖拽并在结束时还原光标。
  本沙箱测试进程是 Low 完整性，UIPI 会阻止输入注入（探测返回不可用），因此默认用 PostMessage
  路径自证单击逻辑，实机真实鼠标点击由你侧确认。
- posPath 目录自建、日志无 ERROR。

最近一次完整结果：见文末“测试记录”。证据：`_临时\pet-test\` 下 `visual-backdrop.png` /
`visual-overlay.png` / `menu-open.png` / `click-menu.png` / `test-summary.txt` / `pet.log`。

## DPI 说明（混合 DPI 实测，2026-11-02 更新）

真机为**混合 DPI**（不是最初任务书写的 100%）：

| 显示器 | 物理坐标 | DPI | 工作区（物理） |
|---|---|---|---|
| DISPLAY1 主屏 | (0,0)–(3840,2160) | 192（200%） | (0,0)–(3592,2160) |
| DISPLAY2 竖屏 | (-1080,0)–(0,1920) | 96（100%） | (-1080,0)–(0,1920) |

物理虚拟屏 = (-1080,0)–(3840,2160)。DSH 网页 `window.devicePixelRatio` 可能是 1（窗口在 100% 副屏），
**不能**拿页面 dpr 当换算倍率；因此本程序在 `SetProcessDPIAware()` 之后用系统 DPI 自动取
`effectiveDpr = max(1, round(GetDpiForSystem()/96))`（本机 = 2），Host 只传 DIP 值即可。
窗口按物理像素创建（system-DPI-aware），拖到 100% 副屏时物理尺寸不变、不会被系统缩放模糊。

## Host 集成注意事项（重要）

- **位置/大小以 Host 为准**：宠物写盘不可靠（见下），拖拽/改大小/复位后请由 Host 落盘
  `POST /api/whale-pet/pos` 并在下次启动用 `config.physical` 传回；pos.json 只作兼容兜底。
- **宠物继承父进程令牌**：实测从 DSH Host 拉起的 pet 进程完整性级别为 **Low**，且父进程若带
  DSH 文件沙箱的写限制（restricted SID），pet 对 `<DSH_HOME>\.whale-pet\`、`%TEMP%` 等普通目录
  **全部写失败**（窗口、渲染、HTTP、位置上报都不受影响）。因此：
  - 不需要磁盘即可工作（HTTP 上报路径已在受限令牌下实测通过）；
  - 若希望有 pet.log：用非沙箱的主进程 spawn，或让 DSH 沙箱给该目录/路径写权限；当前实现有
    4 级路径兜底，全失败则静默。
- **前台/菜单**：Low 完整性进程通常抢不到 Medium 窗口的前台（UIPI），菜单“点击外部关闭”依赖
  watchdog 兜底（已实现）；菜单项鼠标点击不受影响（待真实鼠标确认）。
- **无单实例互斥**：每次启动都会出现一个窗口，Host 侧请以进程句柄/pid 管理自己的实例；
  用户桌面可能短暂出现多个鲸鱼（旧实例心跳失败后 ~12s 自退）。

## 已知限制 / 实机待确认

- 真实鼠标拖拽、透明区点击穿透（`HTTRANSPARENT` 透到其他进程窗口）未用本会话的自动化验证
  （沙箱低完整性下 UIPI 阻止 SendInput 注入），菜单项鼠标点击同理；自动化用 `PostMessage`
  跑同一套 WndProc 代码路径；`-RealMouse` 供非沙箱环境使用。
- 菜单键盘导航（方向键/回车）依赖前台。
- 菜单字体为系统默认，无自定义；无托盘图标（按需求）。
- **气泡文字与音效由 Host 决定**：宠物不读余额、不挑音效；字段缺失时降级为「余额 —」/ 静默跳过。
- **音效**：v1.1.1 起在 STA COM 线程播放 mp3（`mpegvideo`），实测（同为 Low IL 沙箱）
  `mci open ok Ya1.mp3 type=mpegvideo rc=0`；`setaudio` 在没有音量能力的设备上会返回
  rc!=0，仅记日志（音量可能不生效）。真实出声需普通桌面音频设备。
- **动画**由 5 帧 × 60ms UI 定时器重绘驱动，极端低配机器上可能轻微卡顿。
- 窗口采用 system-DPI-aware：本机系统 dpr=2，在 100% 副屏上物理像素尺寸不缩放（视觉上会略小），
  未做 per-monitor 重排。
- 测试脚本在真实桌面运行，用户操作窗口会干扰截图断言，已加干扰检测与自动重试。

## 测试记录

| 日期 | 版本 | 结果 | 备注 |
|---|---|---|---|
| 2026-11-02 00:24 | 1.0.0 | 76/0 | 自动 DPI + Host 中转 pos + 菜单 DIP |
| 2026-11-02 00:47 | 1.0.1 | `-Mode click` 16/16 | 光标 IDC_ARROW + 左键单击弹菜单（PostMessage 路径） |
| 2026-11-02 00:51 | 1.0.1 | **93/0**（attempt 1/3） | 去掉单实例互斥 + 光标 + 单击 + 全量回归 |
| 2026-11-02 01:13 | 1.1.0 | **120/0**（attempt 1/3，一次通过） | 单击 = 弹跳 + 音效 + 余额气泡（poke 接口）+ 全量回归 |
| 2026-11-02 01:19 | 1.1.1 | **125/0**（attempt 1/3） | STA COM 修复：mci open mp3 rc=266→0，真实 mp3 音效断言 |

v1.1.1 exe：53248 bytes（2026-11-02 01:16 构建），SHA256 `74AC2B42CF753F51DAAB4332B2A75AF95609371EE4438EE3059D67252F499209`。
v1.1.0 exe：51712 bytes（2026-11-02 01:12 构建），SHA256 `F0D22F5C43F3246C20B188B2BB29674992DA81F7EF4E6BAE56A9C8423637623D`。
v1.0.1 exe：38912 bytes，SHA256
`D3BD365218EBFB1573380D656F0043C9FD2277730B948A412A1F4D159DEB82F9`。
证据目录 `_临时\pet-test\`：`visual-backdrop.png` / `visual-overlay.png` / `menu-before.png` /
`menu-open.png` / `click-menu.png` / `test-summary.txt` / `pet.log` /
`v1.0.0-mutex-silent-exit.log`（实机旧版静默退出的日志证据）。

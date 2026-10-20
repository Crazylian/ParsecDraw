# ParsecDraw — 观战画笔

用 [Parsec](https://parsec.app) 看朋友打游戏时的画笔互动层：观众在游戏画面上画线指点，笔迹实时同步到玩家屏幕，随 Parsec 串流让所有人看到。

**核心原则：笔迹是纯覆盖层数据，绝不向玩家的机器注入任何输入** —— 玩家全程无感，游戏不会收到任何误操作。

Parsec 本身没有观看方标注功能，市面上也没有"游戏级串流 + 同步画笔覆盖层"的现成工具（Epic Pen 等只有本地覆盖层；Zoom/AnyDesk 有同步画笔但帧率撑不起游戏），所以有了这个项目。

## 使用

### 主机（玩游戏的人）

1. 启动 ParsecDraw，点 **「我是主机」**
2. 程序列出本机局域网地址（`ws://192.168.x.x:8765`），点击行复制，发给观众
3. 保持窗口开着（或直接关掉窗口最小化到托盘——会话继续运行），正常玩游戏。覆盖层全屏透明且点击穿透，不影响任何操作
4. 首次启动会弹 Windows 防火墙授权，勾选"专用网络"并允许

### 观众（画画的人）

1. 启动 ParsecDraw，点 **「我是观众」**，填入主机给的地址和昵称，连接
2. 用 Parsec 观战主机，覆盖层默认就在**绘画态**（屏幕显示十字光标）——直接在画面上画即可
3. 注意：**Ctrl+Alt+D 是双向切换**——绘画态按它会切到观看态（穿透），再按一次才回来；当前状态看工具栏徽标（绘画中/观看中）和按钮文字（完成/开始绘画）
4. 笔迹 5 秒自动淡出（激光笔 0.6 秒）；想保留就按**钉住**；**清空**全消

### 托盘

关闭/最小化窗口 = 隐藏到系统托盘（会话保持运行，托盘图标即应用图标）。左键点图标恢复主面板，右键菜单可**打开**/**退出**。

### 热键（两端通用）

| 热键 | 作用 |
|---|---|
| `Ctrl+Alt+D` | 观众：绘画态 ⇄ 观看态（穿透）；主机：暂停/恢复显示笔迹 |
| `Ctrl+Alt+P` | 钉住当前可见笔迹 |
| `Ctrl+Alt+C` | 清空 |
| 右键（观众） | 快速退出绘画态 |

工具栏可拖动，含 5 色、3 档粗细、画笔/激光切换、撤销（只撤自己最后一笔）。

## 对齐（自动黑边补偿）

- 连接后主机向观众通告屏幕分辨率；观众端自动定位 **Parsec 窗口**，按主机屏幕比例算出视频画面的实际区域——Parsec 加的黑边被扣掉，窗口没铺满/有偏移也不影响落笔位置
- 映射区域变化、进入绘画态时，屏幕会闪现**蓝色虚线框**提示当前可画区域（1.6 秒后消失）
- 找不到 Parsec 窗口时按"整屏 + letterbox"对齐（全屏观看场景）；连不上旧版主机（无分辨率通告）时退回整屏
- 覆盖层只认**主显示器**：观众端 Parsec 窗口请在主显示器上
- 游戏必须用**无边框窗口**模式——独占全屏会盖住覆盖层；观众端仅支持 Windows

## 网络

- 同一局域网直连即可
- 跨公网先自行打通（Tailscale / frp / 端口转发，纯 TCP 8765），再连对应的 `ws://地址:8765`
- v1 无鉴权——这是给朋友间用的小工具，不要把端口暴露到公网

## 构建

```
dotnet build -c Debug
dotnet run --project src/ParsecDraw            # 正常启动
src/.../ParsecDraw.exe --selftest <日志路径>    # 无头自测（协议/状态机/对齐数学/网络回环）
src/.../ParsecDraw.exe --smoke                 # 全链路冒烟：主机 UI + 观众会话连本机，
                                               #   验证 host 分辨率通告→对齐映射，6 秒退出，
                                               #   结果写 %TEMP%\parsec-draw-smoke.log，退出码 0/1
python tools/make_icon.py                      # 重新生成应用图标（纯 stdlib）
```

发布（二选一）：

```
# 零依赖单文件（约 56MB），双击即用
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish/self-contained

# 依赖框架版（约 308KB），需装 .NET 10 Desktop Runtime
dotnet publish -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -o publish/framework-dependent
```

## 实现说明

- **技术栈**：C#/WPF（net10.0-windows），零第三方 NuGet 依赖。选 WPF 而非 Electron 是因为覆盖层常驻打游戏的机器：原生实现内存 40-80MB vs Chromium 的 150-250MB
- **热键**：低级键盘钩子（WH_KEYBOARD_LL）而非 RegisterHotKey——Parsec 客户端获得焦点时会捕获键盘转发给远端主机，系统热键分发收不到按键，而"看着 Parsec 按热键"正是核心场景；钩子在输入链最前端命中即吞掉，按键也不会串进主机
- **对齐**：主机在观众接入时单播屏幕分辨率（含游戏内切分辨率的后续通告），观众端 500ms 轮询定位 Parsec 窗口（EnumWindows + 进程名，找到后只查缓存句柄），客户区按主机宽高比做 letterbox 数学分出视频区；对齐计算零分配、只在矩形变化时重建几何
- **托盘**：`Shell_NotifyIconW` + 隐藏消息窗口 + `TrackPopupMenu` 原生菜单，零依赖手写；图标复用 exe 内嵌的 ApplicationIcon（Win32 资源 ID 1）
- **服务端**：`TcpListener` + 手写 RFC6455 握手 + `WebSocket.CreateFromStream`——HttpListener 绑定局域网地址需要管理员配 URL ACL，这条路线免提权；顺带提供一个 HTTP 状态页（浏览器打开主机地址可见 "ParsecDraw WebSocket server is running"）
- **渲染**：WPF 保留模式。每条笔迹缓存冻结的 `StreamGeometry` + `Pen`，重绘只改透明度不重建几何；没有淡出中的笔迹时渲染循环完全停止（零 CPU）；淡出计时基于各端本地时钟（消息不带时间戳，避免跨机时钟差）
- **协议**：扁平 JSON 消息（begin/point/end/undo/clear/pin/join/host），坐标归一化 `[0,1]`（相对主机画面）；点批次每 33ms 一发；System.Text.Json source-gen 零反射
- **权限**：服务器只转发"撤销自己 begun 的笔迹"，别人的笔迹撤不动；host 分辨率通告只由服务器下发，客户端伪造就地丢弃；clear/pin 人人可用（朋友间民主）

## 目录

```
src/ParsecDraw/
├── Protocol/Messages.cs    # 消息与设置 + JSON source-gen
├── Net/DrawServer.cs       # 主机端 WS 中继（含 host 分辨率通告）
├── Net/DrawClient.cs       # 观众端客户端（自动重连）
├── Draw/StrokeStore.cs     # 笔迹状态机（淡出/钉住/剪除/映射矩形）
├── Draw/DrawCanvas.cs      # 覆盖层渲染
├── Ui/OverlayWindow.*      # 全屏透明覆盖层（穿透互操作/对齐映射）
├── Ui/AlignmentTracker.cs  # 观众端 Parsec 窗口追踪 + letterbox 对齐
├── Ui/ToolbarWindow.*      # 悬浮工具栏
├── Ui/ControlWindow.*      # 控制窗
├── Win32/WinApi.cs         # 点击穿透 / z 序 / 窗口枚举
├── Win32/KeyboardHook.cs   # 低级键盘钩子热键（Parsec 焦点下可用）
├── Win32/TrayIcon.cs       # 系统托盘（Shell_NotifyIcon 互操作）
├── Session.cs              # 本地输入 ⇄ Store ⇄ 网络 的中枢
├── Assets/app.ico          # 应用图标（tools/make_icon.py 生成）
└── SelfTest.cs             # 无头自测
```

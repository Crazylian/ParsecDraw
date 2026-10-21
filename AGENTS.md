# AGENTS.md

- 本项目为 C# / WPF 桌面应用（`net10.0-windows`，WinExe），观战画笔覆盖层
- 第三方依赖（仅两个，新增需用户确认）：H.NotifyIcon.Wpf 2.3.0（托盘）、WPF-UI 3.0.5（Fluent 暗色 UI）
- 序列化：System.Text.Json source-gen（零反射）；网络：手写 TcpListener + WebSocket
- 性能红线：覆盖层空闲时 CPU 必须为 0、内存 <100MB

## Project Structure

```
src/ParsecDraw/
├── Protocol/Messages.cs    # 消息/设置 + JSON source-gen
├── Net/DrawServer.cs       # 主机端 WS 中继（host 分辨率通告）
├── Net/DrawClient.cs       # 观众端客户端（自动重连）
├── Draw/StrokeStore.cs     # 笔迹状态机（淡出/钉住/映射矩形/橡皮命中）
├── Draw/DrawCanvas.cs      # 覆盖层渲染
├── Ui/OverlayWindow.*      # 全屏覆盖层（穿透/对齐映射/原始输入）
├── Ui/AlignmentTracker.cs  # Parsec 窗口追踪 + letterbox 对齐
├── Ui/ToolbarWindow.*      # 工具栏（三选一工具/靠边缩入）
├── Ui/ControlWindow.*      # 控制窗（FluentWindow）
├── Win32/WinApi.cs         # 点击穿透/z 序/窗口枚举
├── Win32/KeyboardHook.cs   # LL 键盘钩子热键
├── Session.cs              # 输入 ⇄ Store ⇄ 网络 中枢
├── Assets/                 # app.ico / pen.cur / eraser.cur（生成器在 tools/）
└── SelfTest.cs             # 无头自测

installer/ParsecDraw.iss         # Inno Setup 安装包脚本（CI 用）
.github/workflows/release.yml    # 发版流水线（tag→Release；dispatch→仅构建）
```

## Coding Style

- 注释、UI 文案、commit 信息一律**中文**；XML doc 注释说明"为什么"而非复述代码
- Win32 互操作用 `LibraryImport` 且**必须显式 `EntryPoint`**（方法名与导出名完全一致时除外）
- 渲染路径零分配：几何/Pen 冻结复用，只在状态变化时 `InvalidateVisual`
- XAML 样式集中放 `Ui/Styles.xaml`（键名 Tb* 前缀）；窗口 Icon 用 `/Assets/...` 根路径 URI
- 坐标一律归一化 `[0,1]`（相对主机画面），经 `StrokeStore.SetMap` 映射矩形进出

## Build Commands

```
dotnet build -c Debug                                  # 构建（须 0 警告 0 错误）
dotnet run --project src/ParsecDraw                    # 正常启动
src/.../ParsecDraw.exe --selftest <日志>               # 无头自测（协议/状态机/对齐/网络/win32）
src/.../ParsecDraw.exe --smoke                         # 全链路冒烟（主机UI+观众会话连本机，
                                                       #   断言：OS级命中/模式事件/画线/橡皮/钩子/托盘，
                                                       #   结果 %TEMP%\parsec-draw-smoke.log，退出码 0/1）
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o publish/self-contained
                                                       # 自包含单文件必须带 IncludeNativeLibrariesForSelfExtract：原生库
                                                       # 不进 bundle 会散落为 exe 旁的独立文件，缺了启动即 DllNotFoundException。
                                                       # 框架依赖单文件不受影响（原生库由共享框架提供）
python tools/make_icon.py                              # 重新生成图标
python tools/make_cursor.py [pen|eraser]               # 重新生成光标
git tag vX.Y.Z && git push origin vX.Y.Z               # 发版：CI 自动构建并发布 Release（单 exe + Setup）
```

UI 视觉改动必须真机截图核验（无头测试看不到渲染层）。

## Never 规则

- Never 用 `RegisterHotKey` 做热键——Parsec 获得焦点时吞键盘，必须走 `KeyboardHook`（LL 钩子）
- Never 把覆盖层 `Background` 设为全透明（alpha=0）——OS 命中测试里不可点击，真实鼠标永远穿底
- Never 用消息投递代替 OS 层验证——PostMessage 绕过投递/命中层；冒烟里的 hit-test/mode-event 断言不可删
- Never 写没有 `EntryPoint` 的 `LibraryImport`（按 C# 方法名找导出，运行时才炸）
- Never 新增 .cur/.ico 等资源而不加进 csproj 的 `<Resource>`（静态构造直接抛异常）
- Never 对 H.NotifyIcon 代码创建不加 `ForceCreate()`（不进可视化树则图标永不出现）
- Never 硬编码 exe 图标资源 ID（apphost 实际是 32512，枚举获取）
- Never 用 `taskkill /IM` 清理测试进程（会误杀用户正跑的实例）——按精确 PID，跑完必须清
- Never 修改 obj/、publish/、SelfTest 之外自动生成的内容
- Never 在渲染/鼠标移动路径做耗时操作或分配
- Never 未确认就改用户可见行为或新增依赖；Never 反复重测未改动的部分
- Never 提交 publish/ 产物与 *.log

## Commit 与版本规范

- 格式：`type: vX.Y.Z —— 中文摘要`（feat / fix / docs / refactor / chore）
- 正文写根因与验证方式；重大教训记入代码注释
- CHANGELOG 每版本条目**只写使用者可见的更改点**——验证结果、选型过程、实现细节一律不进（写 commit 正文）
- 版本一旦定号（含分支上未合并的），行为再有变化**发新版本新增条目**，不改写旧条目；csproj `<Version>` 同步
- **提交即定版**：合并由用户手动掌控、可能取走分支中途的任意提交——一个版本号对应一个自洽提交（代码+csproj 版本+CHANGELOG 同步），版本号与该提交内容严格一致

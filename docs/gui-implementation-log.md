# HybridFileXfer WinUI 3 GUI 实施记录

- 对应方案：[GUI 实施方案](./gui-implementation-plan.md)
- 记录开始日期：2026-10-21
- 当前阶段：P3 完成（双栏文件管理、传输队列、真机/模拟服务端 GUI E2E、路径与缓冲区加固）

## 1. 环境信息

| 项目 | 值 |
|---|---|
| 操作系统 | Windows |
| 工作目录 | `c:\Users\mk\Documents\TRAE\HybridFileXfer` |
| Java | OpenJDK 25.0.2 (Zulu)；Android 构建使用 PyCharm JBR 21.0.10 |
| .NET SDK | `C:\Users\mk\AppData\Local\dotnet-sdk8`（8.0.426）；`C:\Program Files\dotnet` 仅有运行时 |
| Android SDK | `C:\Users\mk\AppData\Local\Android\Sdk`（platform 34、build-tools 34.0.0、NDK 25.1.8937393、CMake 3.10.2.4988404） |
| adb | 项目内 `HybridFileXfer-PC/adb.exe`；设备 serial `emulator-5554` |
| 测试设备 | realme RMX6688 / Android 14 / x86_64 / 2560x1440；wlan0 172.16.1.4 |
| 版本控制 | 当前目录不是 git 仓库 |

> 初次检查时未设置 `ANDROID_HOME` / `ANDROID_SDK_ROOT`，后续在 `C:\Users\mk\AppData\Local\Android\Sdk` 找到 SDK，并成功完成 Android 构建与真机验证。

## 2. 决策记录

| 日期 | 决策 | 原因 |
|---|---|---|
| 2026-10-21 | GUI 采用 WinUI 3 + Java 引擎 sidecar | 复用成熟传输引擎，降低协议回归风险 |
| 2026-10-21 | 不内置 JRE，要求系统 Java 17+ | 遵从用户要求；现有客户端本来就依赖 Java |
| 2026-10-21 | 协议升级到 v3.1 (310) | PC 需要主动浏览/传输，现有协议为手机单向驱动 |
| 2026-10-21 | Android 同时接受 300 和 310 | 旧 CLI 兼容；手机对传混合版本可用；mode 0 代码本来就要保留 |
| 2026-10-21 | 保留手机驱动模式 | 手机对手机传输依赖该模式 |
| 2026-10-21 | PC 控制模式需手机端允许 | 防止任意 PC 直接管理手机文件 |

## 3. 变更记录

| 日期 | 阶段 | 文件 | 变更说明 | 验证 |
|---|---|---|---|---|
| 2026-10-21 | P0 | `docs/gui-implementation-plan.md` | 新增实施方案与协议 v3.1 规格 | 文档评审 |
| 2026-10-21 | P0 | `docs/gui-implementation-log.md` | 新建立项与实施记录 | 文档评审 |
| 2026-10-21 | P0 | `core/ControllerIdentifiers.java`（PC/Android） | 新增 `PC_LIST_FILES(20)`、`PC_DELETE_FILE(21)`、`PC_MKDIR(22)`、`PC_REQUEST_SEND(23)`、`PC_REQUEST_RECEIVE(24)` | PC 编译通过 |
| 2026-10-21 | P0 | `core/HFXService.java`（PC/Android） | 版本常量改为 310/300；新增 `DRIVE_MODE_PHONE/PC`；新增 `writeRemoteFile/readRemoteFile/writeDirectory/readDirectory` 编解码辅助 | PC 编译通过；两份 core 哈希一致 |
| 2026-10-21 | P0 | `core/HFXClient.java`（PC/Android） | 握手发送 310 + driveMode；读取 `driveModeAllowed` 与服务端文件系统/主目录；新增 PC 驱动 API：列目录、删除、新建、手机→PC、PC→手机；控制事务加 `ReentrantLock` | PC 编译通过；已同步至 Android core |
| 2026-10-21 | P0 | `core/callback/ConnectServerCallback.java`（PC/Android） | 新增默认方法 `onDriveModeRejected()` | PC 编译通过 |
| 2026-10-21 | P0 | `droidcore/HFXServer.java` | 接受 300/310；读取 driveMode 与 `pcDriveAllowed`；v3.1 返回服务端文件系统/主目录；新增 PC 指令循环与 list/delete/mkdir/send/receive 处理；断开流程幂等化（`closed` 标志） | 人工检查，待 Android 构建 |
| 2026-10-21 | P0 | `droidcore/StartServerTask.java`、`droidcore/callback/StartServerCallback.java` | 传入 `pcDriveAllowed`；新增 PC 控制模式开始/结束/拒绝回调 | 人工检查，待 Android 构建 |
| 2026-10-21 | P0 | `droidcore/DroidHFXClient.java` | 手机客户端固定使用 `DRIVE_MODE_PHONE` | 人工检查 |
| 2026-10-21 | P0 | `Config.java`、`MainActivity.java`、`activity_main.xml`、`strings.xml`（默认/zh） | 新增「允许电脑管理文件」开关；PC 控制模式禁用手机端传输入口；异常结束恢复主界面 | 人工检查，待 Android 构建 |
| 2026-10-21 | P0 | `HybridFileXfer-PC/jdkcore/JdkHFXClient.java`、`Main.java`、`messages_*.properties` | 新增 driveMode 构造重载；旧 CLI 默认手机驱动；新增 PC 控制被拒绝的提示文案 | PC 编译通过 |
| 2026-10-21 | P0 | `HybridFileXfer-Android/app/build.gradle` | 版本号提升为 310 / 3.1.0 - beta | 人工检查 |
| 2026-10-21 | P1 | `engine/Json.java` | 新增无第三方依赖的极简 JSON 解析/转义工具 | PC 编译通过 |
| 2026-10-21 | P1 | `engine/EngineMain.java` | 新增 `--engine` 模式：connect/list/delete/mkdir/upload/download/disconnect/shutdown/ping；事件输出；进度节流 | PC 编译通过；E2E 通过 |
| 2026-10-21 | P1 | `Main.java` | 启动时识别 `--engine`；ADB forward 支持自定义日志回调，避免污染 JSON stdout | PC 编译通过 |
| 2026-10-21 | P1 | `core/HFXClient.java`（PC/Android） | 新增 `disconnect()` 与 `shutdownServer()`；`freeBuffers()` 改为 public | PC 编译通过；core 同步 |
| 2026-10-21 | P1 | `test/.../MockHFXServer.java` | 新增桌面 Mock 服务端，模拟 v3.1 握手、PC 驱动指令与真实 TCP 传输 | E2E 通过 |
| 2026-10-21 | P1 | `test/.../EngineEndToEndTest.java` | 新增自动化端到端测试：通过 `Main --engine` 入口完成连接、列目录、下载、上传、新建、删除、断开 | `P1 E2E PASS` |
| 2026-10-21 | P1 | `docs/gui-implementation-plan.md` | 补充引擎 IPC 指令/事件字段表 | 文档评审 |
| 2026-10-21 | P1 | `core/WriteFileCall.java`（PC/Android）、`jdkcore/JdkHFXClient.java` | 将文件警告输出从 stdout 改为 stderr，保证引擎 stdout 只有 JSON 行 | PC 编译通过；core 同步；E2E 通过 |
| 2026-10-21 | P1 | `core/bean/Directory.java`、`RemoteFile.java`、`ServerNetInterface.java`（PC/Android） | 移除 JetBrains `@NotNull` 注解，允许 PC 侧无 annotations jar 从源码构建 | PC 全量源码编译通过；core 同步 |
| 2026-10-21 | P1 | `script/build-pc.ps1` | 新增 PC 构建脚本：编译 src、复制 messages、打包 jar | 构建退出码 0；生成 `HybridFileXfer-PC/build/HybridFileXfer.jar` |
| 2026-10-21 | P1 | `droidcore/HFXServer.java` | `disconnect()` 调整为「先关闭控制/传输通道，再释放缓冲区」 | Android 构建 + 真机回归通过 |
| 2026-10-21 | P2 | `EngineMain.java`、`core/HFXClient.java`（PC/Android） | upload/download 返回 boolean 并补发带 id 的 `result` 事件；core 已同步 | PC 编译 + Mock E2E + Android 构建通过 |
| 2026-10-21 | P2 | `HybridFileXfer-Windows/HybridFileXfer.EngineClient/` | 新增 .NET 8 引擎桥接库：启动 Java 子进程、JSON Lines 指令/事件、id 关联、adb PATH 注入 | 构建通过；ping/shutdown 冒烟通过 |
| 2026-10-21 | P2 | `HybridFileXfer-Windows/HybridFileXfer.EngineClient.Smoke/` | 新增控制台冒烟程序 | `PING success=True`、`SHUTDOWN success=True` |
| 2026-10-21 | P2 | `HybridFileXfer-Windows/HybridFileXfer.Gui/` | 新增 WinUI 3 骨架：连接表单、状态/通道/速度、远程目录、日志、MainViewModel | `dotnet build` 0 警告 0 错误 |
| 2026-10-21 | P2 | `HybridFileXfer.Gui.csproj` | 复制 `HybridFileXfer.jar`、`adb.exe` 与 DLL 到输出目录；unpackaged self-contained | 输出目录内容齐全 |
| 2026-10-21 | P3 | `HybridFileXfer.Gui/Services/LocalFileService.cs` | 本机驱动器/目录枚举、回收站删除、新建目录、打开文件 | GUI 本地浏览/新建/删除通过 |
| 2026-10-21 | P3 | `HybridFileXfer.Gui/Services/PathCompat.cs` | Windows 文件名校验：非法字符、保留名、结尾点/空格 | 新建目录测试通过 |
| 2026-10-21 | P3 | `HybridFileXfer.Gui/TransferJob.cs` | 传输任务模型：方向、状态、进度、速度、错误 | 队列 UI 正常 |
| 2026-10-21 | P3 | `HybridFileXfer.Gui/MainViewModel.cs` | 双栏状态、远程/本机操作、传输队列串行执行、事件映射 | GUI E2E 通过 |
| 2026-10-21 | P3 | `HybridFileXfer.Gui/MainWindow.xaml`、`MainWindow.xaml.cs` | 双栏布局、工具栏、路径栏、传输队列、日志、确认/命名对话框、双击打开、x:Name 便于自动化 | 构建 + UI 自动化通过 |
| 2026-10-21 | P3 | `core/bean/Directory.java`（PC/Android） | Windows 目标路径额外处理保留名、结尾点/空格、`.`/`..` | `DirectoryPathTest PASS`；core 同步 |
| 2026-10-21 | P3 | `core/HFXService.java`（PC/Android） | 新增 `activeTransfers` 计数与 `hasActiveTransfers()`；发送/接收包裹 try/finally | PC 编译 + E2E + Android 构建通过 |
| 2026-10-21 | P3 | `core/HFXClient.java`（PC/Android） | `freeBuffers()` 在仍有传输线程时不清理缓冲区队列 | core 同步 |
| 2026-10-21 | P3 | `droidcore/HFXServer.java` | 断开时若仍有传输线程，延迟释放 Native 缓冲区；`freeBuffersNative()` 加同步 | 真机异常断开通过 |
| 2026-10-21 | P3 | `droidcore/DroidHFXClient.java` | 手机客户端同样延迟释放 Native 缓冲区 | Android 构建通过 |
| 2026-10-21 | P3 | `test/.../DirectoryPathTest.java` | 新增 Windows 路径兼容测试 | `DirectoryPathTest PASS` |
| 2026-10-21 | P3 | `test/.../MockServerMain.java` | 新增 Mock 服务端 GUI 联调入口 | 成功输出端口并服务 |
| 2026-10-21 | P3 | `test/.../EngineEndToEndTest.java` | 增加文件夹下载/上传及嵌套文件断言 | `P1 E2E PASS` |

## 4. 验证记录

| 日期 | 验证项 | 命令/方式 | 结果 |
|---|---|---|---|
| 2026-10-21 | 环境检查 | `java -version` / `javac -version` | Java 25 可用 |
| 2026-10-21 | Android SDK 检查 | 检查环境变量与 `local.properties` | 初始未设置环境变量；SDK 实际位于 `C:\Users\mk\AppData\Local\Android\Sdk`。未重建 `local.properties`，通过 `ANDROID_HOME` 构建 |
| 2026-10-21 | PC 全量编译 | `javac --release 8 -implicit:none` 编译全部 27 个源文件（已移除 JetBrains 注解依赖） | 退出码 0，无错误 |
| 2026-10-21 | core 双端一致性 | 对 PC/Android `core/*.java` 做 MD5 比对 | 全部一致，无差异 |
| 2026-10-21 | Android 构建 | `gradlew clean assembleDebug --no-daemon`（JAVA_HOME=PyCharm JBR 21，ANDROID_HOME=本机 SDK） | `BUILD SUCCESSFUL`，47 个任务全部执行 |
| 2026-10-21 | APK 安装 | `adb install -r app-debug.apk` | `Success` |
| 2026-10-21 | 权限配置 | `appops set ... MANAGE_EXTERNAL_STORAGE allow` | 成功 |
| 2026-10-21 | 真机 UI 验证 | 仅勾选 USB_ADB，勾选「允许电脑管理文件」，启动服务 | USB_ADB 显示「等待连接」；PC 控制开关禁用 |
| 2026-10-21 | 真机端到端 | `java -jar HybridFileXfer.jar --engine` + `adb forward tcp:5740` | 连接、列目录、下载、上传、新建、删除全部成功 |
| 2026-10-21 | 真机文件校验 | PC/手机 MD5 比对（hello.txt、中文.txt、1mb.bin、上传文件） | 全部一致 |
| 2026-10-21 | 真机 ADB 模式 | `connect mode=adb` | adb forward、连接、列目录、下载成功 |
| 2026-10-21 | 异常断开 | 20MB 传输中杀掉引擎进程 | App 未崩溃、PID 存活、UI 恢复「启动服务器并等待连接」 |
| 2026-10-21 | 释放顺序回归 | 重新构建 APK、安装、连接、列目录、下载、正常断开 | 通过；UI 恢复启动状态，无 AndroidRuntime 异常 |
| 2026-10-21 | 引擎 stdout 编码 | E2E 事件写入 UTF-8 日志，控制台仅输出 ASCII 摘要 | 中文不再乱码；事件流 UTF-8 正确 |
| 2026-10-21 | P1 编译 | `javac --release 8` 编译 src + test | 退出码 0，无错误 |
| 2026-10-21 | P1 端到端 | `EngineEndToEndTest`（`Main --engine` + Mock 服务端 + 真实 TCP） | `P1 E2E PASS` |
| 2026-10-21 | P1 测试覆盖 | 连接、列目录、下载 0B/5B/21B 中文名/1MB、上传、新建、删除、断开、关闭 | 文件内容/大小/路径断言全部通过；无 error 事件；stdout 全部为 JSON 行 |
| 2026-10-21 | P1 最终复验 | 重新编译 src + test，执行 `EngineEndToEndTest`，再做 core MD5 比对 | `COMPILE_EXIT=0`、`E2E_EXIT=0`、`CORE_SYNC_OK` |
| 2026-10-21 | .NET 8 SDK | 使用 dotnet-install 安装到 `C:\Users\mk\AppData\Local\dotnet-sdk8` | 8.0.426 可用 |
| 2026-10-21 | EngineClient 构建 | `dotnet build HybridFileXfer.EngineClient.Smoke` | 0 警告 0 错误 |
| 2026-10-21 | EngineClient 冒烟 | 启动 `HybridFileXfer.jar --engine`，发送 ping/shutdown | `PING success=True`、`SHUTDOWN success=True` |
| 2026-10-21 | WinUI 3 构建 | `dotnet build HybridFileXfer.Gui -c Debug -p:Platform=x64`，WindowsAppSDK 1.8.260803003 | `BUILD_EXIT=0`，0 警告 0 错误 |
| 2026-10-21 | WinUI 3 启动 | 运行输出目录的 `HybridFileXfer.Gui.exe` | 进程存活，窗口标题 `多轨快传 GUI`，HWND 非 0 |
| 2026-10-21 | GUI 连接自动化 | UI Automation 点击「连接」 | 被权限审批限流拦截，未执行；桥接已由冒烟测试验证 |
| 2026-10-21 | Android 增量构建 | `gradlew assembleDebug --no-daemon`（boolean API 变更后） | `BUILD SUCCESSFUL`；HFXClient.class 时间戳晚于源码 |
| 2026-10-21 | PC 全量源码编译 | 编译 `HybridFileXfer-PC/src` 全部 27 个文件（无 annotations jar） | 退出码 0 |
| 2026-10-21 | jar 打包 | `script/build-pc.ps1` | 退出码 0；jar 约 0.07MB |
| 2026-10-21 | jar 引擎入口 | `java -jar HybridFileXfer.jar --engine` + ping/shutdown | 输出 ready/ping/bye，退出码 0 |
| 2026-10-21 | Android 真机联调 | 真机 / 模拟器 | 已执行：连接、列目录、双向传输、新建、删除、ADB 模式、异常断开均通过 |
| 2026-10-21 | P3 WinUI 构建 | `dotnet build HybridFileXfer.Gui -c Debug -p:Platform=x64` | 0 警告 0 错误 |
| 2026-10-21 | P3 GUI 启动 | 运行 `HybridFileXfer.Gui.exe` | 窗口标题 `多轨快传 GUI`，进程存活 |
| 2026-10-21 | P3 本机浏览 | UI Automation 设置本地路径并刷新 | `C:\Users\mk` 86 项；临时目录 3 项；状态文本正确 |
| 2026-10-21 | P3 本机新建 | 通过 GUI 对话框创建 `created-by-ui` | 目录创建成功 |
| 2026-10-21 | P3 本机删除 | 通过 GUI 删除 `pc-upload.txt`，确认「继续」 | 文件移入回收站，路径不存在 |
| 2026-10-21 | P3 GUI + Mock 连接 | IP 127.0.0.1 + Mock 端口 | 连接成功，通道 `MOCK`，列出 3 项 |
| 2026-10-21 | P3 GUI 下载（Mock） | 选中 `remote-a.txt` 点击下载 | 文件写入本地，内容 `remote-a` |
| 2026-10-21 | P3 GUI 上传（Mock） | 选中 `gui-upload.txt` 点击上传 | Mock 根目录出现文件，内容 `uploaded-by-gui` |
| 2026-10-21 | P3 远程新建/删除（Mock） | GUI 对话框新建 `gui-created`，再选中删除 | 创建成功，删除后路径不存在 |
| 2026-10-21 | P3 GUI + 真机连接 | ADB 模式连接手机 | 通道 `USB_ADB`，远程 `/storage/emulated/0` 22 项 |
| 2026-10-21 | P3 GUI 真机下载 | 选中 `1mb.bin` 下载 | 本地 1048576 字节；MD5 `c35cc7d8d91728a0cb052831bc4ef372` 与手机一致 |
| 2026-10-21 | P3 GUI 真机上传 | 选中 `pc-upload.txt` 上传 | 手机文件内容 `upload-from-pc-p3` |
| 2026-10-21 | P3 GUI 断开 | 点击断开 | 手机 UI 恢复「启动服务器并等待连接」，无崩溃 |
| 2026-10-21 | P3 缓冲区加固验证 | 20MB 下载中强杀 Java 引擎 | 手机 App PID 2550 存活，UI 恢复，无 AndroidRuntime 异常 |
| 2026-10-21 | P3 文件夹传输 | `EngineEndToEndTest` 下载/上传 `subdir/nested.txt` | 嵌套文件内容断言通过 |
| 2026-10-21 | P3 路径兼容 | `DirectoryPathTest` | `CON.txt`→`_CON.txt`、`COM1.log`→`_COM1.log`、结尾点/空格去除、`..`→`_` 全部通过 |
| 2026-10-21 | P3 Android 构建 | 新代码 `gradlew assembleDebug --no-daemon` | `BUILD SUCCESSFUL`，APK 安装成功 |

## 5. 问题与处理

| 日期 | 问题 | 处理 |
|---|---|---|
| 2026-10-21 | 工作目录非 git 仓库，大改无法回滚 | 记录风险；建议在正式开发前初始化 git 或做备份 |
| 2026-10-21 | 本地缺少 Android SDK，无法构建 App | 已找到本机 SDK；使用 PyCharm JBR 21 作为 JAVA_HOME，`gradlew clean assembleDebug` 构建成功，并完成真机验证 |
| 2026-10-21 | 本地缺少 `annotations-24.0.1.jar` | 已移除 core bean 的 `@NotNull` 注解，PC 侧现在可无外部依赖构建 |
| 2026-10-21 | PC 客户端主动断开会触发服务端 PC 控制结束通知，造成重复恢复 UI | 增加 `disconnectRequestedByUi` 标志；UI 主动断开时不再回调 `onPcDriveModeStopped` |
| 2026-10-21 | 断开时释放缓冲区可能与仍在收尾的传输线程竞争 | P3 已完成：发送/接收增加 `activeTransfers` 计数；Android 服务端/客户端在仍有传输线程时延迟释放 Native 缓冲区。20MB 下载中强杀引擎验证通过 |
| 2026-10-21 | PC 控制模式下的手机端 UI 状态尚未真机验证 | 已真机验证：连接后禁用传输入口；正常断开与异常断开均恢复「启动服务器并等待连接」 |
| 2026-10-21 | Windows 控制台显示中文文件名乱码 | 已通过「事件写入 UTF-8 日志 + 控制台输出 ASCII 摘要」解决；GUI 仍需按 UTF-8 解码引擎 stdout |
| 2026-10-21 | 现有 `out/artifacts` 的 jar 未包含 P1 引擎类 | 已新增 `script/build-pc.ps1` 并生成新的 `HybridFileXfer-PC/build/HybridFileXfer.jar` |
| 2026-10-21 | Mock 服务端不等同于真实 Android 服务端 | 已完成真机验证：连接、列目录、双向传输、新建、删除、异常断开均通过 |
| 2026-10-21 | ADB 模式依赖 `adb` 在 PATH 中或当前目录存在 `adb.exe` | 真机测试通过；P2 的 `EngineClient` 已把 adb 路径所在目录注入子进程 PATH，并默认复制项目 adb.exe 到 GUI 输出目录 |
| 2026-10-21 | Windows 保留文件名、结尾点/空格、长路径未专项测试 | 保留名、结尾点/空格已在 P3 修复并通过 `DirectoryPathTest`；长路径仅启用清单，压力测试留待 P4 |
| 2026-10-21 | 工作目录仍不是 git 仓库 | 未擅自初始化；建议正式开发前 `git init` 并提交当前可运行版本 |
| 2026-10-21 | `dotnet --list-sdks` 未列出 SDK | 已安装 .NET 8.0.426 到 `C:\Users\mk\AppData\Local\dotnet-sdk8`，WinUI 构建通过 |
| 2026-10-21 | `EngineClient` 与命名空间同名，C# 编译器将其解析为命名空间 | 使用 `using Engine = HybridFileXfer.EngineClient.EngineClient;` 别名解决 |
| 2026-10-21 | WinUI 首次构建被沙箱拦截 .NET metadata / AMD DxCache 写入 | 构建与启动改用非沙箱执行；构建、窗口启动均成功 |
| 2026-10-21 | GUI「连接」按钮的 UI Automation 测试被权限审批限流拒绝 | P3 已用 UI Automation 完成 GUI + Mock、GUI + 真机连接与全流程验证 |
| 2026-10-21 | WinUI 3 的 `Window` 不支持 `Window.Resources` | 将 `DataTemplate` 资源移到根 `Grid.Resources` |
| 2026-10-21 | `LocalFileService` 的 `out` 变量触发 CS0165 | 先初始化 `error` 再传入 `out` |
| 2026-10-21 | UI Automation 首次选择对话框输入框时选中了只读日志框 | 通过 `Name="名称"` 精确匹配对话框输入框；属于测试脚本问题，不是产品缺陷 |
| 2026-10-21 | UI Automation 只能看到已虚拟化的 ListView 项 | GUI 渲染与操作正常；自动化按当前可见项选择。后续可给 `ListViewItem` 增加 `AutomationProperties.Name` 改善可访问性 |
| 2026-10-21 | 当前传输没有优雅取消，取消需断开连接 | 记录为 P4 功能项；P3 的「取消等待」只取消未开始的队列任务 |
| 2026-10-21 | Windows 长路径只做了 `longPathAware` 清单，未做超长路径压力测试 | 记录为 P4 验证项 |

## 6. 待办

- [x] P0：协议常量、握手、服务端命令循环、客户端驱动 API（PC 编译 + 真机验证通过）
- [x] P0：Android PC 控制开关与回调（Android 构建 + 真机验证通过）
- [x] P0：PC 侧编译验证
- [x] P0：Android Gradle 构建与真机联调
- [x] P1：Java 引擎 JSON IPC
- [x] P1：控制台端到端验证（Mock + 真机均通过）
- [x] P1：PC 构建脚本与 jar 重新打包
- [x] P2：WinUI 3 工程骨架 + Java 引擎桥接
- [x] P3：双栏文件管理与传输队列
- [x] P3：本机/远程新建、删除、回收站、文件夹传输
- [x] P3：传输线程/缓冲区释放加固、Windows 路径兼容性
- [ ] P4：优雅取消传输、长路径压力测试
- [ ] P4：安装包、CI、多语言、许可证

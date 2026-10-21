# HybridFileXfer WinUI 3 GUI 实施方案

- 文档版本：v0.1
- 创建日期：2026-10-21
- 当前阶段：P3 完成（双栏文件管理、传输队列、真机/模拟服务端 GUI E2E、路径与缓冲区加固）
- 关联文档：[GUI 实施记录](./gui-implementation-log.md)

## 1. 背景与目标

本项目当前由三部分组成：

- Android 应用：服务端（`HFXServer`），同时内含手机对手机时的被动客户端（`DroidHFXClient`）。
- PC Java 客户端：命令行工具，被动响应手机端指令。
- .NET 启动器：仅负责启动 Java 客户端。

目标是在 Windows 上增加一个 **WinUI 3 桌面 GUI**，具备完整的文件管理能力：

1. 连接手机（ADB / IP）。
2. 双栏浏览 PC 与手机目录。
3. 从 PC 主动发起双向传输。
4. 新建文件夹、删除、刷新、路径跳转、书签。
5. 显示多通道状态、总速度、单通道速度与传输进度。
6. 对危险操作提供确认与错误提示。

## 2. 已确认决策

| 决策项 | 结论 | 说明 |
|---|---|---|
| GUI 框架 | WinUI 3 + Windows App SDK + .NET 8+ | 仅支持 Windows 10 1809+ / Windows 11 |
| 传输引擎 | Java sidecar，复用现有 `HybridFileXfer.jar` | GUI 通过 IPC 控制，不重写分块传输 |
| JRE 分发 | **不内置 JRE** | 要求系统已安装 Java 17+；GUI 启动时检测并允许指定路径 |
| 协议版本 | v3.0 (300) → **v3.1 (310)** | 新增 PC 驱动模式与文件管理指令 |
| 旧版兼容 | Android 服务端同时接受 300 / 310 | 旧 PC CLI、旧手机客户端仍可连接；新 GUI 只使用 310 |
| 手机驱动模式 | 保留（driveMode = 0） | 手机对手机传输依赖该模式，不能删除 |
| PC 驱动模式 | 新增（driveMode = 1） | GUI 文件管理使用 |
| 发布方式 | GUI 与 Android App 同版本发布 | GUI 要求 App 3.1+ |

## 3. 总体架构

```text
┌──────────────────────────────────────────────┐
│ WinUI 3 GUI (C# / .NET 8+)                   │
│ 连接页 / 双栏文件管理 / 传输队列 / 设置       │
└──────────────────────┬───────────────────────┘
                       │ JSON Lines（stdin/stdout 或命名管道）
┌──────────────────────▼───────────────────────┐
│ Java Engine（HybridFileXfer.jar --engine）   │
│ 会话管理 / 远程目录 / 双向传输 / 事件输出     │
└──────────────────────┬───────────────────────┘
                       │ HFXC v3.1
                       │ 控制通道 + N 个传输通道
┌──────────────────────▼───────────────────────┐
│ Android App 3.1（HFXServer）                 │
│ {PC 控制模式 | 手机驱动模式}                  │
└──────────────────────────────────────────────┘
```

设计原则：

- Java 引擎是**唯一会话所有者**，同一时间只允许一个控制事务。
- GUI 的本地文件浏览、本地删除/新建可直接用 C# 实现；远程目录与传输必须经过引擎。
- 所有控制指令串行执行；传输期间 GUI 必须排队其他文件操作。
- 传输异常后不做半恢复，直接断开并重连。

## 4. 协议 v3.1 规范

### 4.1 版本与模式常量

| 常量 | 值 | 说明 |
|---|---|---|
| `CLIENT_HEADER` | `"HFXC"` | 4 字节 ASCII |
| `VERSION_CODE` | `310` | 新客户端/服务端 |
| `VERSION_CODE_LEGACY` | `300` | 旧客户端兼容 |
| `DRIVE_MODE_PHONE` | `0` | 手机端驱动（旧 CLI、手机对传） |
| `DRIVE_MODE_PC` | `1` | PC 端驱动（GUI 文件管理） |

### 4.2 握手流程

#### v3.1 客户端（GUI 引擎 / 新版 CLI / 新版手机客户端）

1. Client → Server：`"HFXC"` + `int 310` + `byte driveMode`
2. Server → Client：`boolean versionOK`
   - 若 false：`int serverVersion`，关闭。
3. Server → Client：`boolean driveModeAllowed`
   - 仅 v3.1 发送；`driveMode == 0` 时恒为 true。
   - 若客户端请求 `driveMode == 1` 且手机端未允许，返回 false，客户端提示后关闭。
4. Server → Client：`int ipCount` + 网卡列表（格式同 v3.0）
5. 传输通道连接协商（格式同 v3.0）
6. 缓冲区数量协商（格式同 v3.0）
7. Client → Server：`int clientFileSystem` + `UTF clientHomeDir`
8. Server → Client：`int serverFileSystem` + `UTF serverHomeDir`（v3.1 新增）
9. 连接完成：
   - `driveMode == 0`：客户端进入被动循环；服务端等待手机 UI 驱动。
   - `driveMode == 1`：服务端启动客户端指令循环；客户端通过驱动 API 发指令。

#### v3.0 客户端（旧 CLI / 旧手机客户端）

1. Client → Server：`"HFXC"` + `int 300`
2. Server 接受 300，不读取 `driveMode`，不发送 `driveModeAllowed`，不发送服务端信息。
3. 其余流程与旧版完全一致。
4. 服务端进入手机驱动模式，由手机 UI 发起所有操作。

### 4.3 新增指令（客户端 → 服务端）

| ID | 名称 | 方向 | 请求载荷 | 响应 |
|---|---|---|---|---|
| 20 | `PC_LIST_FILES` | PC → 手机 | `UTF path` | `int count`（-1 表示失败）+ 文件条目 |
| 21 | `PC_DELETE_FILE` | PC → 手机 | `UTF path` | `boolean success` |
| 22 | `PC_MKDIR` | PC → 手机 | `UTF parent` + `UTF child` | `boolean success` |
| 23 | `PC_REQUEST_SEND` | PC 请求手机发送 | `int count` + `UTF path` × count + `Directory phoneSource` + `Directory pcTarget` | 进入传输，完成确认走现有控制通道 |
| 24 | `PC_REQUEST_RECEIVE` | PC 请求手机接收 | `Directory pcSource` + `Directory phoneTarget` | 进入传输，完成确认走现有控制通道 |

说明：

- 指令 ID 从 20 开始，避开旧指令 `0–11`。
- 文件条目格式沿用 v3.0：
  - `UTF name` + `UTF path` + `long lastModified` + `long size` + `boolean isDirectory`
- `Directory` 格式沿用 v3.0：
  - `UTF path` + `int fileSystem`
- 所有整型、UTF 字符串均为 **大端序**，与 `DataByteChannel` 一致。

### 4.4 双向传输流程

#### 手机 → PC（`PC_REQUEST_SEND`）

1. PC 发送 `PC_REQUEST_SEND`，携带手机文件列表、手机源目录、PC 目标目录。
2. 服务端读取后调用 `sendFiles(fileList, phoneSource, pcTarget, callback)`。
3. PC 调用 `receiveFiles(callback)`。
4. 完成确认沿用 `sendFiles`/`receiveFiles` 现有控制通道布尔握手。
5. 服务端命令循环在传输结束后继续读取下一条指令。

#### PC → 手机（`PC_REQUEST_RECEIVE`）

1. PC 发送 `PC_REQUEST_RECEIVE`，携带 PC 源目录、手机目标目录。
2. 服务端调用 `receiveFiles(callback)`。
3. PC 调用 `sendFiles(fileList, pcSource, phoneTarget, callback)`。
4. 完成确认同上。
5. 传输结束后命令循环继续。

### 4.5 串行化与错误处理

- 客户端所有控制事务由 `ReentrantLock` 串行化；传输期间持锁，其他文件操作排队。
- 服务端命令循环单线程执行；传输期间不处理新指令。
- 传输过程中出现 IOException / 通道断开：停止命令循环、关闭所有连接、通知 UI。
- 取消传输 v1 采用「关闭传输通道 → 双方报错 → 重连」，不实现优雅取消。
- 未知指令：视为协议错误，关闭连接。

### 4.6 兼容矩阵

| 客户端 | 服务端 | 结果 |
|---|---|---|
| 旧 CLI 3.0 | 新 App 3.1 | ✅ 手机驱动模式可用 |
| 新 CLI 3.1（driveMode=0） | 新 App 3.1 | ✅ 手机驱动模式可用 |
| 新 GUI 3.1（driveMode=1） | 新 App 3.1 | ✅ 完整文件管理 |
| 新 GUI 3.1 | 旧 App 3.0 | ❌ 版本不匹配，提示升级手机端 |
| 旧手机客户端 3.0 | 新 App 3.1 | ✅ 服务端兼容 300 |
| 新手机客户端 3.1（driveMode=0） | 新 App 3.1 | ✅ 手机对传 |

## 5. 模块设计

### 5.1 Android 端

改动文件：

- `core/ControllerIdentifiers.java`：新增 20–24 指令。
- `core/HFXService.java`：版本常量、文件/目录编解码辅助方法。
- `core/HFXClient.java`：握手升级、driveMode、服务端信息、驱动 API。
- `core/HFXServer.java`：接受 300/310、读取 driveMode、发送服务端信息、命令循环、文件管理指令处理。
- `core/bean/*`：不变。
- `droidcore/DroidHFXClient.java`：构造时传入 `DRIVE_MODE_PHONE`。
- `droidcore/StartServerTask.java`：传入 `pcDriveAllowed`。
- `droidcore/callback/StartServerCallback.java`：新增 PC 控制模式回调（默认方法）。
- `Config.java`：新增 `pcDriveAllowed` 配置，默认 true。
- `MainActivity.java`：PC 控制开关、连接成功/停止状态处理。
- `res/layout/activity_main.xml`：新增开关。
- `res/values*/strings.xml`：新增文案。

不修改：

- AIDL `IIOService` / `IOServiceImpl`。
- `NativeMemory` / JNI。
- `ReadFileCall` / `WriteFileCall` / `SendFileCall` / `ReceiveFileCall`。
- 缓冲区协商逻辑。

### 5.2 Java 核心与引擎

- 共享 core 两份（Android、PC）必须保持一致。
- 新增客户端驱动 API：
  - `listServerFiles(path)`
  - `deleteServerFile(path)`
  - `mkdirServer(parent, child)`
  - `downloadFiles(files, localDir, remoteDir, callback)`
  - `uploadFiles(files, localDir, remoteDir, callback)`
- 新增 `--engine` 模式：
  - stdin 接收 JSON Lines 指令；stdout 只输出 JSON Lines 事件；Java 异常堆栈可走 stderr。
  - 编码固定 UTF-8。
  - 指令：`connect`、`disconnect`、`list`、`delete`、`mkdir`、`upload`、`download`、`shutdown`、`ping`。
  - 事件：`ready`、`state`、`log`、`connected`、`files`、`result`、`progress`、`speed`、`channel`、`complete`、`error`。
  - 进度事件节流到约 10Hz。
  - 指令同步执行：传输期间不读取下一条指令，GUI 需自行排队。

**指令字段**

| cmd | 字段 |
|---|---|
| `connect` | `mode`（adb/ip）、`address`、`port`（默认 5740）、`device`、`homeDir`、`driveMode`（默认 1） |
| `list` | `path` |
| `delete` | `path` |
| `mkdir` | `parent`、`child` |
| `upload` | `paths`（PC 文件绝对路径数组）、`sourceDir`（PC）、`targetDir`（手机） |
| `download` | `paths`（手机文件绝对路径数组）、`sourceDir`（手机）、`targetDir`（PC） |
| `disconnect` / `shutdown` / `ping` | 无 |

所有请求可带 `id`，响应事件会原样带回。

**事件字段**

| event | 字段 |
|---|---|
| `ready` | `protocol`、`java` |
| `state` | `state` |
| `log` | `source`、`message` |
| `connected` | `id`、`channels[]`、`serverHomeDir`、`serverFileSystem` |
| `files` | `id`、`path`、`entries[]`（`name/path/lastModified/size/isDirectory`）、可选 `error` |
| `result` | `id`、`success` |
| `progress` | `direction`（upload/download）、`path`、`current`、`total` |
| `speed` | `upload`、`download`、`channels[]`（`name/upload/download`） |
| `channel` | `name`、`state`、`traffic`、`time`、可选 `message` |
| `complete` | `direction`、`traffic`、`time` |
| `error` | 可选 `id`、`message` |

- 默认 CLI 行为不变；新 CLI 使用 `driveMode=0`。

### 5.3 WinUI 3 端

实际工程结构（P2 已完成）：

```text
HybridFileXfer-Windows/
├── HybridFileXfer.EngineClient/          # 纯 .NET 8，Java 进程 + JSON Lines 桥接
├── HybridFileXfer.Gui/                   # WinUI 3 应用（unpackaged self-contained）
└── HybridFileXfer.EngineClient.Smoke/    # 控制台桥接冒烟测试
```

P2 已实现：

- 连接表单：ADB/IP、地址、端口、PC 主目录、Java 路径、adb 路径。
- 状态、通道列表、速度、远程目录列表、日志。
- `MainViewModel`（轻量 MVVM）负责连接/断开/列目录与引擎事件分发。
- 引擎进程桥接：`EngineClient` 启动 `java -jar HybridFileXfer.jar --engine`，
  发送 JSON Lines 指令，按 `id` 关联响应，并转发状态/进度/速度/错误事件。
- adb 路径：支持传目录或 adb.exe 路径，桥接会把其目录加入子进程 PATH。
- 默认把 `HybridFileXfer.jar`、`adb.exe` 及其 DLL 复制到 GUI 输出目录。

P3 已实现：

- 左侧本机、右侧手机的双栏文件管理。
- 本机驱动器/目录浏览、路径跳转、上一级、刷新、双击打开。
- 手机目录浏览、上一级、刷新、双击进入/下载。
- 本机新建目录、删除（回收站）、打开文件。
- 手机新建目录、删除。
- 双向传输、传输队列、文件/整体进度、各通道速度、错误显示。
- 传输期间禁用远程操作，避免控制通道事务交错。
- 队列「取消等待」；当前传输取消需断开连接（P4 再实现优雅取消）。
- Windows 路径兼容：保留名、结尾点/空格；清单启用 `longPathAware`。
- 传输缓冲区延迟释放：传输线程仍在运行时等待其结束。

尚未实现：

- 拖拽、书签、设置页、冲突自动改名、当前传输优雅取消。
- 完整 MVVM 框架重构（当前为轻量 MVVM）。

打包：先 non-packaged self-contained；稳定后 MSIX。

### 5.4 构建与发布

- 已新增 PC 构建脚本 `script/build-pc.ps1`，输出 `HybridFileXfer-PC/build/HybridFileXfer.jar`。
- 引擎启动方式：`java -jar HybridFileXfer.jar --engine`。
- 已移除 core bean 中的 JetBrains `@NotNull` 注解，使 PC 侧可无外部依赖地从源码构建。
- Android 本地构建环境：SDK `C:\Users\mk\AppData\Local\Android\Sdk`（platform 34 / build-tools 34.0.0 / NDK 25.1.8937393），JAVA_HOME 使用 JBR 21；`gradlew clean assembleDebug` 已通过。
- CI：Android 现有 Gradle 构建 + PC jar 构建 + WinUI 构建（windows-latest）。
- 不内置 JRE；GUI 启动时检测 `java -version`，支持自定义 Java 路径。
- 许可证：GUI 源码按 GPL-3.0 发布，保留第三方 MIT 许可声明。

## 6. 里程碑

| 阶段 | 内容 | 验收标准 |
|---|---|---|
| P0 | 协议常量、握手、服务端命令循环、客户端驱动 API、Android 开关 | PC 侧可编译；协议帧与规格一致 |
| P1 | Java 引擎 JSON IPC + 控制台验证 | 连接、列手机目录、手机→PC、PC→手机、删除/新建全部通过 |
| P2 | WinUI 3 工程骨架 + 连接页 | 可连接、显示通道状态、显示远程目录 |
| P3 | 双栏文件管理 + 传输队列 + 进度 | 已完成：本机/手机浏览、双向传输、新建/删除、异常恢复均验证通过 |
| P4 | 安装包、CI、多语言、许可证、日志 | 可安装、可发布 |

Go/No-Go：P1 控制台端到端验证通过后再投入 P3 的 UI 工作量。

当前状态：P0–P3 已完成；Mock/真机 E2E、Android 构建、WinUI 3 构建与 GUI 全流程（连接、浏览、双向传输、新建、删除、异常断开）均通过。P4 待开始。

## 7. 主要风险

| 风险 | 等级 | 对策 |
|---|---|---|
| 控制通道事务交错 | 高 | 单事务锁 + 服务端单线程命令循环 |
| 协议字节序/格式偏差 | 高 | 复用 `DataByteChannel` 与共享 core；对帧做抓包对比 |
| Android 双模式状态冲突 | 高 | driveMode=1 时禁用手机端传输页 |
| 传输异常后通道错位 | 高 | 异常即断线重连 |
| Windows 文件名兼容 | 中 | 补齐保留名、结尾空格/点、长路径 |
| 无 Android SDK 时无法本地构建 | 中 | CI 构建；本地至少做 PC 侧编译 |
| GPL 合规 | 低（必须） | GUI 源码开源、保留许可证 |

## 8. 测试计划

- 协议编解码：固定字节序列断言（UTF、int、long、boolean、Directory）。
- 连接：ADB、IP、单网卡、多网卡、版本不匹配、PC 控制被拒绝。
- 文件管理：列大目录、中文/日文/韩文名、空目录、权限不足。
- 传输：0 字节、1MB 边界、>4GB、大量小文件、双向、断线中断。
- 性能：与 Java CLI 同条件对比，吞吐不低于其约 90%。
- Windows 路径：保留名、长路径、结尾点/空格。

## 9. 文档与记录规则

- 本文件为实施方案与协议规格的唯一来源。
- 每次代码改动后，在 [实施记录](./gui-implementation-log.md) 追加：
  - 日期、阶段、改动文件、验证方式、结果、遗留问题。
- 协议字段如变更，先更新本文件第 4 节，再改代码。

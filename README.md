# GTA Radio Bridge

将 Apple Music（或任何系统音频）实时桥接到 GTA V Self Radio 电台。

## 系统要求

- Windows 10 / 11
- .NET 8 SDK（https://dotnet.microsoft.com/download）
- GTA V PC 版
- Apple Music for Windows（Microsoft Store 版或 iTunes）
- **需要以管理员身份运行**（ETW 内核监听需要提升权限）

## 构建步骤

```bash
# 1. 进入项目目录
cd GTARadioBridge

# 2. 还原 NuGet 包
dotnet restore

# 3. 构建 Release 版本
dotnet build -c Release

# 4. 发布为独立可执行文件（可选）
dotnet publish -c Release -r win-x64 --self-contained true -o ./publish
```

构建产物位于 `bin/Release/net8.0-windows10.0.19041.0/` 或 `./publish/`。

## 使用方法

1. **右键** `GTARadioBridge.exe` → **以管理员身份运行**
2. 确认 "GTA V User Music Path" 路径正确（通常无需修改）
3. 打开 Apple Music，开始播放
4. 点击 **▶ Start Bridge**
5. 启动 GTA V，进入载具，将电台切换到 **Self Radio**
6. 使用游戏原生的 `=`（下一首）和 `-`（上一首）切歌

## 工作原理

```
Apple Music 播放
    ↓ WASAPI Loopback 捕获系统音频
编码为 MP3（NAudio + LAME）
    ↓ 写入 4 个轮换槽位文件
GTA V Self Radio 按文件名顺序播放
    ↓ ETW 监听 GTA 的文件读取事件
程序感知 GTA 的播放进度，提前准备下一个槽位
```

## 槽位文件

程序在 User Music 目录创建以下文件：

```
gbridge_slot_00.mp3
gbridge_slot_01.mp3
gbridge_slot_02.mp3
gbridge_slot_03.mp3
```

这些文件会被持续覆盖。停止 Bridge 后可安全删除。

## 注意事项

- **仅限个人使用**：通过 WASAPI 捕获 Apple Music 音频违反 Apple 服务条款，请勿用于任何商业目的。
- **Self Radio 在 GTA Online 中正常工作**，此程序不注入游戏进程，不违反 Rockstar 反作弊规则。
- 首次使用前，在 GTA V 设置 → 音频 → 执行一次"完整扫描"，让游戏识别槽位文件。
- 若 ETW 监听无法正常工作，程序仍可运行，但槽位轮换将依赖计时器而非精确进度。

## 扩展其他流媒体服务

在 `Core/NowPlayingWatcher.cs` 中，GSMTC API 会自动检测当前活跃的媒体会话，
因此 Spotify、YouTube Music 等只要在 Windows 上播放，捕获逻辑同样适用。

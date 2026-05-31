# GTA Radio Bridge

将 Apple Music（或任何系统音频）实时桥接到 GTA V Self Radio 电台。

> **免责声明**：本项目仅供个人学习研究使用。通过 WASAPI 捕获
> Apple Music 音频可能违反 Apple 服务条款，请勿用于任何商业目的。

---

## 功能

- 自动捕获 Apple Music 播放的音频，注入 GTA V Self Radio
- 支持 GTA 原生切歌键（`=` 下一首 / `-` 上一首）
- 系统托盘运行，不占用任务栏
- 显示当前播放曲目信息
- 支持 GTA Online（不注入游戏进程，不触发反作弊）

## 工作原理

- Apple Music 播放
- ↓ WASAPI Loopback 捕获系统音频
- 编码为 MP3（NAudio + LAME）
- ↓ 写入 4 个轮换槽位文件
- GTA V Self Radio 按文件名顺序播放
- ↓ ETW 监听 GTA 的文件读取进度
- 自动提前准备下一个槽位

## 系统要求

- Windows 10 / 11
- GTA V PC 版
- Apple Music for Windows（Microsoft Store 版或 iTunes）
- 以**管理员身份运行**（ETW 内核监听需要提升权限）

## 下载

前往 [Releases](../../releases) 页面下载最新版本。

## 使用方法

1. 右键 `GTARadioBridge.exe` → **以管理员身份运行**
2. 确认 User Music 路径正确（通常无需修改）
3. 打开 Apple Music，开始播放
4. 点击 **▶ Start Bridge**
5. 启动 GTA V，进入载具，切换到 **Self Radio**
6. 使用 `=` / `-` 切歌

**首次使用**：在 GTA V 设置 → 音频 → 执行一次「完整扫描」，
让游戏识别槽位文件。

## 槽位文件

程序在 User Music 目录自动创建以下文件，停止后可安全删除：

- gbridge_slot_00.mp3
- gbridge_slot_01.mp3
- gbridge_slot_02.mp3
- gbridge_slot_03.mp3

## 扩展其他流媒体服务

程序使用 Windows GSMTC API 监听当前活跃媒体会话，
Spotify、YouTube Music 等服务的音频同样可以被捕获，
无需修改代码。

## 构建

见 [编译说明](../../wiki) 或直接使用 GitHub Actions 自动构建。

## License

[MIT](LICENSE)

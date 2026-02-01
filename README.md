# Quest 安装与输入工具（WPF / .NET 8）
#本软件含AI编写内容

这是一个将以下操作封装为 Windows 图形界面的工具：
- 安装 APK（`adb install -r`）
- 向头显输入文字（`adb shell input text`）

## 快速使用
1. 在 Meta Quest 手机 App 中开启 **开发者模式**（Developer Mode）。
2. 用 **支持数据传输** 的 USB 数据线连接 Quest 和电脑。
3. 戴上头显，点击 **“允许 USB 调试”**（建议勾选“始终允许”）。
4. 打开本工具，状态显示 **“已连接”** 后即可使用。

## 放置 ADB 文件
请把 Android SDK `platform-tools` 里的 3 个文件放到本项目的 `adb/` 目录：
- `adb.exe`
- `AdbWinApi.dll`
- `AdbWinUsbApi.dll`

## 发布（生成 exe）
```powershell
dotnet publish -c Release -r win-x64 /p:PublishSingleFile=true /p:SelfContained=true
```

输出目录：
`bin\Release\net8.0-windows\win-x64\publish\`

## 日志
日志自动保存到：
`%LocalAppData%\QuestADBTool\logs\`


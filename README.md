软件代码中包含由 AI "主导编写"的内容，如有不适还请见谅。

# QuestADBTool

QuestADBTool 是一个面向 Quest 头显的Windows图形化 ADB 工具。

项目当前定位：
- 头显无需联网
- 头显无需额外安装软件
- 仅通过 USB 数据线连接

## 功能概览

### 1. 设备连接与诊断
- 自动检测头显连接状态
- 区分未连接、未授权、已连接等状态
- 提供“修复连接”操作
- 提供“连接诊断”面板
- 可查看 ADB 版本、设备识别状态、常见故障建议

### 2. APK 安装
- 选择单个或多个 APK 加入安装队列
- 支持拖拽 APK 到安装区域
- 支持覆盖安装 `-r`
- 支持允许降级 `-d`
- 支持允许测试包 `-t`
- 显示安装成功/失败统计
- 支持失败项重试
- 支持暂停后续、停止队列、导出安装结果

### 3. APK 预解析
- 读取 APK 文件名与大小
- 解析包名

### 4. 文本输入
- 向头显当前聚焦输入框发送文字

### 5. 应用管理
- 读取应用列表
- 支持名称或包名搜索
- 启动应用
- 卸载应用
- 强制停止应用
- 清除应用数据
- 导出 APK
- 打开应用详情页
- 查看权限
- 复制包名

### 6. 屏幕工具
- 截图并自动拉取到本地
- 开始录屏
- 停止录屏并自动保存到本地

### 7. 自定义脚本
- 支持逐行执行 ADB 命令

### 8. 日志与历史记录
- 显示运行日志
- 记录关键操作历史

## 适用场景

- 给 Quest 安装 APK
- 快速查看头显基本状态
- 管理已安装应用
- 导出设备内 APK

## 运行要求

- Windows
- Quest 头显
- 一根支持数据传输的 USB 数据线
- 头显已开启开发者模式

## 首次使用

1. 在 Meta Quest 手机 App 中开启开发者模式。
2. 使用支持数据传输的 USB 线连接头显和电脑。
3. 戴上头显，允许 USB 调试。
4. 打开 QuestADBTool。
5. 当界面显示“已连接”后即可开始使用。

如果界面显示未授权或 offline：
- 先确认头显内是否弹出了 USB 调试授权框
- 优先尝试主板 USB 2.0 Type-A 接口
- 可在设备页使用“修复连接”和“连接诊断”

## ADB 组件说明

项目发布包内应包含 `adb/` 目录，至少需要以下文件：

- `adb.exe`
- `AdbWinApi.dll`
- `AdbWinUsbApi.dll`

如果你是自己运行源码或手动组装发布包，请确认这些文件存在于程序目录下的 `adb/` 文件夹内。

## 本地输出目录

### 截图
默认保存到：

`图片\QuestADBTool`

### 录屏
默认保存到：

`视频\QuestADBTool`

### 安装结果导出
默认保存到：

`文档\QuestADBTool`

### 日志
默认保存到：

`%LocalAppData%\QuestADBTool\logs\`

## 构建与发布

### 调试构建

```powershell
dotnet build QuestADBTool_Package.sln
```

### 发布

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -Configuration Release -Runtime win-x64 -VersionTag 0.5.3
```

发布脚本会完成以下工作：
- `dotnet publish`
- 整理发布目录
- 复制 `adb/` 组件
- 生成 zip 安装包

默认输出：
- `artifacts\publish\Release-win-x64\`
- `artifacts\QuestADBTool_0.5.3.zip`

## 项目结构

```text
QuestADBTool_Package/
├─ Views/                      页面 UI
├─ adb/                        ADB 组件
├─ artifacts/                  构建与打包产物
├─ MainWindow.xaml             主窗口布局
├─ MainWindow.xaml.cs          主窗口主逻辑
├─ MainWindow.UsbFeatures.cs   USB 诊断 / APK 解析 / 应用扩展逻辑
├─ QuestADBTool.csproj         项目文件
├─ publish.ps1                 发布脚本
├─ update.json                 更新清单
└─ CHANGELOG.md                更新日志
```

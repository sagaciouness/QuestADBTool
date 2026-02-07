using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace QuestADBTool;

public partial class MainWindow : Window
{
    private string AdbPath => System.IO.Path.Combine(AppContext.BaseDirectory, "adb", "adb.exe");

    private readonly object _logLock = new();
    private bool _isBusy;
    private DispatcherTimer? _autoDetectTimer;
    private DispatcherTimer? _installProgressTimer;
    private DateTime _installStartAt;

    private Brush? _installCardDefaultBorderBrush;
    private Thickness _installCardDefaultBorderThickness;
    private Brush? _installCardDefaultBackground;

    private int _installSuccessCount;
    private int _installFailCount;

    private string _logFilePath = "";

    public MainWindow()
    {
        InitializeComponent();

        _installCardDefaultBorderBrush = InstallCardBorder.BorderBrush;
        _installCardDefaultBorderThickness = InstallCardBorder.BorderThickness;
        _installCardDefaultBackground = InstallCardBorder.Background;

        _installProgressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _installProgressTimer.Tick += (_, __) =>
        {
            if (InstallProgressPanel.Visibility == Visibility.Visible)
            {
                var elapsed = DateTime.Now - _installStartAt;
                InstallProgressText.Text = $"正在安装... 已耗时 {elapsed:mm\\:ss}";
            }
        };

        UpdateInstallStats();
        ResetApkPreview();
        SetStatusDot("idle");
        InitLogFile();
        ShowFirstRunTip();

        Loaded += async (_, __) => await RefreshStatus();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshStatus();

    private async void RestartAdb_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "正在修复连接...");
        try
        {
            if (!EnsureAdbExists()) return;

            await RunAdb("kill-server");
            await RunAdb("start-server");
            await RefreshStatus();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Guide_Click(object sender, RoutedEventArgs e) => ShowGuideDialog();

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_logFilePath)) return;
        var dir = System.IO.Path.GetDirectoryName(_logFilePath);
        if (dir == null) return;

        Process.Start(new ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true
        });
    }

    private void ApkPathBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => UpdateApkPreview(ApkPathBox.Text.Trim());

    private void PickApk_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Filter = "APK Files (*.apk)|*.apk|All Files (*.*)|*.*",
            Title = "选择 APK"
        };

        if (ofd.ShowDialog() == true)
        {
            ApkPathBox.Text = ofd.FileName;
            UpdateApkPreview(ofd.FileName);
        }
    }

    private async void InstallApk_Click(object sender, RoutedEventArgs e)
        => await InstallApkFromPath(ApkPathBox.Text);

    private void InstallArea_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var isValid = TryGetSingleApkFromDropData(e.Data, out _);
        e.Effects = isValid ? DragDropEffects.Copy : DragDropEffects.None;
        SetInstallDropVisual(true, isValid);
    }

    private void InstallArea_DragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        SetInstallDropVisual(false, true);
    }

    private async void InstallArea_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        SetInstallDropVisual(false, true);

        if (!TryGetSingleApkFromDropData(e.Data, out var apkPath))
        {
            MessageBox.Show("请拖入单个 .apk 文件。", "拖拽内容无效",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ApkPathBox.Text = apkPath;
        UpdateApkPreview(apkPath);
        await InstallApkFromPath(apkPath);
    }

    private static bool TryGetSingleApkFromDropData(IDataObject data, out string apkPath)
    {
        apkPath = "";
        if (!data.GetDataPresent(DataFormats.FileDrop)) return false;
        var files = data.GetData(DataFormats.FileDrop) as string[];
        if (files == null || files.Length != 1) return false;
        var candidate = files[0];
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (!candidate.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) return false;
        if (!System.IO.File.Exists(candidate)) return false;
        apkPath = candidate;
        return true;
    }

    private async Task InstallApkFromPath(string apkPath)
    {
        SetBusy(true, "正在安装应用...");
        StartInstallProgress();

        try
        {
            if (!EnsureAdbExists()) return;

            var apk = apkPath.Trim();
            if (string.IsNullOrWhiteSpace(apk) || !System.IO.File.Exists(apk))
            {
                MessageBox.Show("APK 路径无效，请重新选择有效的 .apk 文件。", "无效的 APK",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var state = await GetDeviceState();
            if (!GuardDeviceState(state)) return;

            var result = await RunAdb($"install -r \"{apk}\"");

            var elapsed = DateTime.Now - _installStartAt;
            HandleInstallResult(apk, result.ExitCode, result.StdOut, result.StdErr, elapsed);

            await RefreshStatus();
        }
        finally
        {
            StopInstallProgress();
            SetBusy(false);
        }
    }

    private async void SendText_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "正在发送文字...");
        try
        {
            if (!EnsureAdbExists()) return;
            var text = InputTextBox.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                MessageBox.Show("请先输入要发送的文字。", "未输入文字",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var state = await GetDeviceState();
            if (!GuardDeviceState(state)) return;

            var encoded = EncodeForAdbInputText(text);
            await RunAdb($"shell input text {encoded}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(LogBox.Text ?? ""); } catch { }
    }

    private const string AuthorUrl = "https://space.bilibili.com/1570010855";

    private void OpenAuthor_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = AuthorUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"[OpenAuthor] 打开作者链接失败: {ex.Message}");
            MessageBox.Show("打开作者链接失败，请检查系统默认浏览器设置。", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateInstallStats()
    {
        InstallSuccessBadge.Text = $"成功 {_installSuccessCount}";
        InstallFailBadge.Text = $"失败 {_installFailCount}";
    }

    private void StartInstallProgress()
    {
        _installStartAt = DateTime.Now;
        InstallProgressPanel.Visibility = Visibility.Visible;
        InstallProgressBar.IsIndeterminate = true;
        InstallProgressText.Text = "正在安装... 已耗时 00:00";
        _installProgressTimer?.Start();
    }

    private void StopInstallProgress()
    {
        _installProgressTimer?.Stop();
        InstallProgressPanel.Visibility = Visibility.Collapsed;
    }

    private void SetInstallDropVisual(bool isActive, bool isValid)
    {
        if (!isActive)
        {
            if (_installCardDefaultBorderBrush != null) InstallCardBorder.BorderBrush = _installCardDefaultBorderBrush;
            InstallCardBorder.BorderThickness = _installCardDefaultBorderThickness;
            if (_installCardDefaultBackground != null) InstallCardBorder.Background = _installCardDefaultBackground;
            return;
        }

        InstallCardBorder.BorderThickness = new Thickness(2);
        InstallCardBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isValid ? "#2563EB" : "#DC2626"));
        InstallCardBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isValid ? "#EFF6FF" : "#FEF2F2"));
    }

    private void HandleInstallResult(string apkPath, int exitCode, string stdOut, string stdErr, TimeSpan elapsed)
    {
        var fileName = System.IO.Path.GetFileName(apkPath);
        var combined = $"{stdOut}\n{stdErr}";
        var success = exitCode == 0 && combined.Contains("Success", StringComparison.OrdinalIgnoreCase);

        if (success)
        {
            _installSuccessCount++;
            UpdateInstallStats();
            MessageBox.Show($"安装成功：{fileName}\n耗时：{elapsed:mm\\:ss}", "安装完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _installFailCount++;
        UpdateInstallStats();

        var reason = ExtractInstallFailureReason(combined);
        var advice = ExtractInstallFailureAdvice(combined);
        MessageBox.Show(
            $"安装失败：{fileName}\n耗时：{elapsed:mm\\:ss}\n\n原因：{reason}\n建议：{advice}\n\n可点击“打开问题日志”查看完整输出。",
            "安装失败",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static string ExtractInstallFailureReason(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return "未收到 adb 输出。";

        var failureMatch = Regex.Match(output, @"Failure\s*\[(?<reason>[^\]]+)\]", RegexOptions.IgnoreCase);
        if (failureMatch.Success)
            return failureMatch.Groups["reason"].Value;

        var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 ? lines[0].Trim() : "安装失败，未获取到具体错误信息。";
    }

    private static string ExtractInstallFailureAdvice(string output)
    {
        var upper = output.ToUpperInvariant();

        if (upper.Contains("INSTALL_FAILED_VERSION_DOWNGRADE"))
            return "目标设备已安装更高版本，先卸载旧版本或改用降级安装。";
        if (upper.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE"))
            return "签名不一致，先卸载设备上的旧版本后再安装。";
        if (upper.Contains("INSTALL_FAILED_ALREADY_EXISTS"))
            return "设备上已存在同包名应用，先卸载旧版本后再安装。";
        if (upper.Contains("INSTALL_PARSE_FAILED"))
            return "APK 包可能损坏或不完整，建议重新下载。";
        if (upper.Contains("INSTALL_FAILED_INSUFFICIENT_STORAGE"))
            return "设备存储空间不足，请先清理空间。";
        if (upper.Contains("INSTALL_FAILED_TEST_ONLY"))
            return "该 APK 是测试包。";
        if (upper.Contains("INSTALL_FAILED_OLDER_SDK"))
            return "设备系统版本过低，不满足该 APK 要求。";
        if (upper.Contains("INSTALL_FAILED_NO_MATCHING_ABIS"))
            return "APK 架构与设备不匹配。";
        if (upper.Contains("OFFLINE") || upper.Contains("NO DEVICES/EMULATORS FOUND") || upper.Contains("EOF"))
            return "ADB 连接不稳定。建议优先改用主板 USB 2.0 Type-A 口，并重新授权 USB 调试后重试。";

        return "请检查日志中的 ADB 输出，并确认设备连接、授权与 APK 来源。";
    }

    private void UpdateApkPreview(string apkPath)
    {
        if (string.IsNullOrWhiteSpace(apkPath) || !System.IO.File.Exists(apkPath))
        {
            ResetApkPreview();
            return;
        }

        var fi = new System.IO.FileInfo(apkPath);
        ApkInfoFileText.Text = $"文件：{fi.Name}";
        ApkInfoSizeText.Text = $"大小：{FormatFileSize(fi.Length)}";
        ApkInfoPackageText.Text = "包名：-";
        ApkInfoVersionText.Text = "版本：-";
    }

    private void ResetApkPreview()
    {
        ApkInfoFileText.Text = "文件：-";
        ApkInfoSizeText.Text = "大小：-";
        ApkInfoPackageText.Text = "包名：-";
        ApkInfoVersionText.Text = "版本：-";
    }

    private static string FormatFileSize(long sizeBytes)
    {
        if (sizeBytes < 1024) return $"{sizeBytes} B";
        if (sizeBytes < 1024 * 1024) return $"{sizeBytes / 1024.0:F1} KB";
        if (sizeBytes < 1024 * 1024 * 1024) return $"{sizeBytes / (1024.0 * 1024.0):F1} MB";
        return $"{sizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    private void ShowGuideDialog()
    {
        MessageBox.Show(
            "新手引导：\n\n" +
            "1. 用支持数据传输的数据线把 Quest 连接到电脑。\n" +
            "2. 戴上头显，点击“允许电脑进行 USB 调试”（建议勾选“始终允许”）。\n" +
            "3. 回到软件，状态显示“已连接”后即可安装应用或发送文字。\n\n" +
            "常见问题：\n" +
            "- 显示“需要授权”：说明头显正在等待你点击允许。\n" +
            "- 识别不到设备或频繁 offline：优先把 USB 线接到主板 USB 2.0 Type-A 口，再重新检测。",
            "新手引导",
            MessageBoxButton.OK,
            MessageBoxImage.Information
        );
    }

    private void ShowFirstRunTip()
    {
        var flagPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuestADBTool",
            "first_run.flag");

        if (System.IO.File.Exists(flagPath)) return;
        ShowGuideDialog();

        var dir = System.IO.Path.GetDirectoryName(flagPath);
        if (!string.IsNullOrWhiteSpace(dir)) System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(flagPath, "ok");
    }

    private async Task RefreshStatus()
    {
        if (!EnsureAdbExists())
        {
            StatusText.Text = "缺少 ADB 组件，请将 adb.exe 和 dll 放到程序目录的 adb 文件夹。";
            SetActionEnabled(false);
            return;
        }

        var state = await GetDeviceState();
        switch (state)
        {
            case DeviceState.NotFound:
                StatusText.Text = "未检测到 Quest，请用数据线连接头显。";
                SetStatusDot("bad");
                SetActionEnabled(false);
                break;
            case DeviceState.Unauthorized:
                StatusText.Text = "需要授权，请戴上头显点击“允许 USB 调试”。";
                SetStatusDot("warn");
                SetActionEnabled(false);
                break;
            case DeviceState.Connected:
                StatusText.Text = "已连接，可以开始使用。";
                SetStatusDot("ok");
                SetActionEnabled(true);
                break;
            default:
                StatusText.Text = "状态未知。";
                SetStatusDot("idle");
                SetActionEnabled(false);
                break;
        }
    }

    private void SetActionEnabled(bool enabled)
    {
        InstallApkButton.IsEnabled = enabled && !_isBusy;
        SendTextButton.IsEnabled = enabled && !_isBusy;
    }

    private bool GuardDeviceState(DeviceState state)
    {
        if (state == DeviceState.NotFound)
        {
            MessageBox.Show(
                "未检测到 Quest。\n\n请确认：\n- Quest 已开机\n- 数据线支持数据传输\n- 已开启开发者模式",
                "设备未连接",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (state == DeviceState.Unauthorized)
        {
            MessageBox.Show(
                "需要在头显确认授权。\n\n请戴上 Quest 并点击“允许 USB 调试”，然后点击“重新检测”。",
                "需要授权",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private bool EnsureAdbExists()
    {
        if (!System.IO.File.Exists(AdbPath))
        {
            AppendLog("[错误] 未在 ./adb 找到 adb.exe");
            MessageBox.Show(
                "未找到 adb.exe。\n\n请把以下文件放到程序目录的 adb 文件夹：\n- adb.exe\n- AdbWinApi.dll\n- AdbWinUsbApi.dll",
                "缺少 ADB 组件",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
        return true;
    }

    private enum DeviceState { Connected, Unauthorized, NotFound, Unknown }

    private async Task<DeviceState> GetDeviceState()
    {
        var result = await RunAdb("devices", logCommand: false);
        var lines = result.StdOut.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.EndsWith("\tdevice", StringComparison.OrdinalIgnoreCase)) return DeviceState.Connected;
            if (line.EndsWith("\tunauthorized", StringComparison.OrdinalIgnoreCase)) return DeviceState.Unauthorized;
        }

        if (result.StdErr.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
            return DeviceState.Unauthorized;

        return DeviceState.NotFound;
    }

    private static string EncodeForAdbInputText(string s)
    {
        var sb = new StringBuilder(s.Length * 2);
        foreach (var ch in s)
        {
            if (ch == ' ') { sb.Append("%s"); continue; }
            if ("&|<>();".IndexOf(ch) >= 0 || ch == '"' || ch == '\\') { sb.Append('_'); continue; }
            if (ch == '\r' || ch == '\n') continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private void InitLogFile()
    {
        var logDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuestADBTool",
            "logs");
        System.IO.Directory.CreateDirectory(logDir);

        var fileName = $"log_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
        _logFilePath = System.IO.Path.Combine(logDir, fileName);

        SafeAppendFile(
            $"=== QuestADBTool Log ==={Environment.NewLine}" +
            $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
            $"OS: {Environment.OSVersion}{Environment.NewLine}" +
            $"AppBase: {AppContext.BaseDirectory}{Environment.NewLine}" +
            $"========================{Environment.NewLine}{Environment.NewLine}");

        AppendLog($"[Log] Log file: {_logFilePath}");
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunAdb(string args, bool logCommand = true)
    {
        if (logCommand) AppendLog($"> adb {args}");

        var psi = new ProcessStartInfo
        {
            FileName = AdbPath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var p = new Process { StartInfo = psi };
        p.Start();
        var stdOut = await p.StandardOutput.ReadToEndAsync();
        var stdErr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();

        if (!string.IsNullOrWhiteSpace(stdOut)) AppendLog(stdOut.TrimEnd());
        if (!string.IsNullOrWhiteSpace(stdErr)) AppendLog(stdErr.TrimEnd());
        AppendLog($"(exit={p.ExitCode})");
        AppendLog("");

        return (p.ExitCode, stdOut, stdErr);
    }

    private void AppendLog(string text)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {text}";
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
        SafeAppendFile(line + Environment.NewLine);
    }

    private void SafeAppendFile(string content)
    {
        if (string.IsNullOrWhiteSpace(_logFilePath)) return;
        lock (_logLock) { System.IO.File.AppendAllText(_logFilePath, content, Encoding.UTF8); }
    }

    private void SetBusy(bool isBusy, string? message = null)
    {
        _isBusy = isBusy;
        if (InstallApkButton != null) InstallApkButton.IsEnabled = !isBusy;
        if (SendTextButton != null) SendTextButton.IsEnabled = !isBusy;
        if (PickApkButton != null) PickApkButton.IsEnabled = !isBusy;
        if (ReplaceInstallCheckBox != null) ReplaceInstallCheckBox.IsEnabled = !isBusy;
        if (AllowDowngradeCheckBox != null) AllowDowngradeCheckBox.IsEnabled = !isBusy;
        if (AllowTestApkCheckBox != null) AllowTestApkCheckBox.IsEnabled = !isBusy;
        if (RefreshButton != null) RefreshButton.IsEnabled = !isBusy;
        if (RestartButton != null) RestartButton.IsEnabled = !isBusy;
        if (OpenLogButton != null) OpenLogButton.IsEnabled = !isBusy;
        if (GuideButton != null) GuideButton.IsEnabled = !isBusy;
        if (ClearLogButton != null) ClearLogButton.IsEnabled = !isBusy;
        if (CopyLogButton != null) CopyLogButton.IsEnabled = !isBusy;

        if (BusyText != null)
        {
            BusyText.Text = message ?? "";
            BusyText.Visibility = (isBusy && !string.IsNullOrWhiteSpace(message))
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        Cursor = isBusy ? Cursors.Wait : Cursors.Arrow;
    }

    private void SetStatusDot(string state)
    {
        var color = state switch
        {
            "ok" => "#22C55E",
            "warn" => "#F59E0B",
            "bad" => "#EF4444",
            _ => "#9CA3AF"
        };
        StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true });
        e.Handled = true;
    }
}

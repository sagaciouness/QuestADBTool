using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Text;
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

    private string _logFilePath = "";

    public MainWindow()
    {
        InitializeComponent();

                SetStatusDot("idle");
InitLogFile();
        ShowFirstRunTip();

        Loaded += async (_, __) => await RefreshStatus();
    }

    // ===== UI handlers =====
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

    
    private void Guide_Click(object sender, RoutedEventArgs e)
    {
        ShowGuideDialog();
    }

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

    private void PickApk_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Filter = "APK Files (*.apk)|*.apk|All Files (*.*)|*.*",
            Title = "选择 APK"
        };
        if (ofd.ShowDialog() == true)
            ApkPathBox.Text = ofd.FileName;
    }

    private async void InstallApk_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "正在安装应用...");
        try
        {

        if (!EnsureAdbExists()) return;

        var apk = ApkPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(apk) || !System.IO.File.Exists(apk))
        {
            MessageBox.Show("APK 路径无效。\n请重新选择有效的 .apk 文件。", "无效的 APK",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var state = await GetDeviceState();
        if (!GuardDeviceState(state)) return;

        await RunAdb($"install -r \"{apk}\"");

        await RefreshStatus();
    
        }
        finally
        {
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
        try
        {
            Clipboard.SetText(LogBox.Text ?? "");
        }
        catch
        {
            // ignore clipboard failures
        }
    }

    private const string AuthorUrl = "https://space.bilibili.com/1570010855";

    private void OpenAuthor_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AuthorUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppendLog($"[OpenAuthor] 打开作者链接失败: {ex.Message}");
            MessageBox.Show("打开作者链接失败，请检查系统默认浏览器设置。", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }


    // ===== First-run tip =====

    private void ShowGuideDialog()
    {
        MessageBox.Show(
            "新手引导：\n\n" +
            "1️⃣ 用支持数据传输的数据线把 Quest 连接电脑\n" +
            "2️⃣ 戴上头显，点击“允许 USB 调试”（建议勾选“始终允许”）\n" +
            "3️⃣ 回到软件，状态显示“已连接”后即可安装应用或发送文字\n\n" +
            "常见问题：\n" +
            "• 显示“需要授权”：说明头显正在等待你点允许\n" +
            "• 识别不到设备：换一根支持数据传输的线，或点“修复连接”再“重新检测”",
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
            "first_run.flag"
        );

        if (System.IO.File.Exists(flagPath)) return;

        ShowGuideDialog();

System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(flagPath)!);
        System.IO.File.WriteAllText(flagPath, "ok");
    }

    // ===== State / enable-disable =====
    private async Task RefreshStatus()
    {
        if (!EnsureAdbExists())
        {
            StatusText.Text = "缺少组件 — 请将 adb.exe 和 dll 放入程序目录的 adb 文件夹";
            SetActionEnabled(false);
            return;
        }

        var state = await GetDeviceState();
        switch (state)
        {
            case DeviceState.NotFound:
                StatusText.Text = "未检测到 Quest ｜请用数据线连接头显";
                SetStatusDot("bad");
                SetActionEnabled(false);
                break;
            case DeviceState.Unauthorized:
                StatusText.Text = "需要授权 ｜请戴上头显点击“允许 USB 调试”";
                SetStatusDot("warn");
                SetActionEnabled(false);
                break;
            case DeviceState.Connected:
                StatusText.Text = "已连接 ｜可以开始使用";
                SetStatusDot("ok");
                SetActionEnabled(true);
                break;
            default:
                StatusText.Text = "状态未知";
                SetStatusDot("idle");
                SetActionEnabled(false);
                break;
        }
    }

    private void SetActionEnabled(bool enabled)
    {
        InstallApkButton.IsEnabled = enabled;
        SendTextButton.IsEnabled = enabled;
    }

    private bool GuardDeviceState(DeviceState state)
    {
        if (state == DeviceState.NotFound)
        {
            MessageBox.Show(
                "未检测到 Quest。\n\n请确认：\n• Quest 已开机\n• 数据线支持数据传输\n• 已开启开发者模式",
                "设备未连接",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (state == DeviceState.Unauthorized)
        {
            MessageBox.Show(
                "需要在头显确认授权。\n\n请戴上 Quest 并点击“允许 USB 调试”。\n然后点击“重新检测”。",
                "需要授权",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    // ===== ADB detection =====
    private bool EnsureAdbExists()
    {
        if (!System.IO.File.Exists(AdbPath))
        {
            AppendLog("[错误] 未在 ./adb 找到 adb.exe");
            MessageBox.Show(
                "未找到 adb.exe。\n\n请把以下文件放到程序目录的 adb 文件夹：\n• adb.exe\n• AdbWinApi.dll\n• AdbWinUsbApi.dll",
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

            if (line.EndsWith("\tdevice", StringComparison.OrdinalIgnoreCase))
                return DeviceState.Connected;

            if (line.EndsWith("\tunauthorized", StringComparison.OrdinalIgnoreCase))
                return DeviceState.Unauthorized;
        }

        if (result.StdErr.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
            return DeviceState.Unauthorized;

        return DeviceState.NotFound;
    }

    // ===== input text encoding =====
    private static string EncodeForAdbInputText(string s)
    {
        // Practical encoding:
        // - Space -> %s
        // - Remove CR/LF
        // - Replace shell-dangerous characters with underscore
        var sb = new StringBuilder(s.Length * 2);
        foreach (var ch in s)
        {
            if (ch == ' ')
            {
                sb.Append("%s");
                continue;
            }

            if ("&|<>();".IndexOf(ch) >= 0 || ch == '"' || ch == '\\')
            {
                sb.Append('_');
                continue;
            }

            if (ch == '\r' || ch == '\n') continue;

            sb.Append(ch);
        }
        return sb.ToString();
    }

    // ===== logging to UI + file =====
    private void InitLogFile()
    {
        var logDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuestADBTool",
            "logs"
        );

        System.IO.Directory.CreateDirectory(logDir);

        var fileName = $"log_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
        _logFilePath = System.IO.Path.Combine(logDir, fileName);

        SafeAppendFile(
            $"=== QuestADBTool Log ==={Environment.NewLine}" +
            $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
            $"OS: {Environment.OSVersion}{Environment.NewLine}" +
            $"AppBase: {AppContext.BaseDirectory}{Environment.NewLine}" +
            $"========================{Environment.NewLine}{Environment.NewLine}"
        );

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

        lock (_logLock)
        {
            System.IO.File.AppendAllText(_logFilePath, content, Encoding.UTF8);
        }
    }


    private void SetBusy(bool isBusy, string? message = null)
    {
        _isBusy = isBusy;

        // disable key action buttons
        if (InstallApkButton != null) InstallApkButton.IsEnabled = !isBusy;
        if (SendTextButton != null) SendTextButton.IsEnabled = !isBusy;
        if (PickApkButton != null) PickApkButton.IsEnabled = !isBusy;

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

        this.Cursor = isBusy ? Cursors.Wait : Cursors.Arrow;
    }

    private void SetStatusDot(string state)
    {
        if (StatusDot == null) return;

        string color = state switch
        {
            "ok" => "#22C55E",   // green
            "warn" => "#F59E0B", // amber
            "bad" => "#EF4444",  // red
            _ => "#9CA3AF"       // gray
        };

        StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = e.Uri.AbsoluteUri,
            UseShellExecute = true
        });

        e.Handled = true;
    }

}

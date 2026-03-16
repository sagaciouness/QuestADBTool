using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
namespace QuestADBTool;
public partial class MainWindow
{

    private async void ExpReadDisplay_Click(object sender, RoutedEventArgs e) => await ReadExperimentalDisplayInfoAsync(showNotice: true);

    private async void ExpApplyResolution_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ExpResolutionWidthBox.Text.Trim(), out var width) ||
            !int.TryParse(ExpResolutionHeightBox.Text.Trim(), out var height) ||
            width < 3200 || width > 5408 ||
            height < 1728 || height > 2912)
        {
            ShowNotice("分辨率范围：宽 3200~5408，高 1728~2912。", "warn");
            return;
        }

        SetBusy(true, "正在应用分辨率...");
        try
        {
            if (!await EnsureExperimentReadyAsync()) return;
            var result = await RunAdb($"shell wm size {width}x{height}");
            if (result.ExitCode != 0)
            {
                ShowNotice("分辨率设置失败，请查看日志。", "warn");
                return;
            }
            ShowNotice($"已应用分辨率：{width}x{height}", "info");
            await ReadExperimentalDisplayInfoAsync(showNotice: false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ExpResetResolution_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "正在重置分辨率...");
        try
        {
            if (!await EnsureExperimentReadyAsync()) return;
            var result = await RunAdb("shell wm size reset");
            if (result.ExitCode != 0)
            {
                var readResult = await RunAdb("shell wm size", logCommand: false);
                var physical = TryParsePhysicalSize(readResult.StdOut);
                if (physical == null)
                {
                    ShowNotice("分辨率重置失败，且未读取到 Physical size。", "warn");
                    return;
                }

                var fallbackResult = await RunAdb($"shell wm size {physical.Value.Width}x{physical.Value.Height}");
                if (fallbackResult.ExitCode != 0)
                {
                    ShowNotice("分辨率重置失败（fallback 也失败），请查看日志。", "warn");
                    return;
                }

                ShowNotice($"系统不支持 reset，已回写 Physical size：{physical.Value.Width}x{physical.Value.Height}", "info");
                await ReadExperimentalDisplayInfoAsync(showNotice: false);
                return;
            }
            ShowNotice("分辨率已重置为系统默认。", "info");
            await ReadExperimentalDisplayInfoAsync(showNotice: false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ExpApplyRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(ExpRefreshRateBox.Text.Trim(), out var hz) || hz < 72 || hz > 120)
        {
            ShowNotice("刷新率范围：72~120Hz。", "warn");
            return;
        }
        await ApplyRefreshRateAsync(hz);
    }

    private async void ExpRefresh90_Click(object sender, RoutedEventArgs e)
    {
        ExpRefreshRateBox.Text = "90";
        await ApplyRefreshRateAsync(90);
    }

    private async void ExpRefresh120_Click(object sender, RoutedEventArgs e)
    {
        ExpRefreshRateBox.Text = "120";
        await ApplyRefreshRateAsync(120);
    }

    private async void ExpCaptureScreen_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "正在截图...");
        try
        {
            if (!await EnsureExperimentReadyAsync()) return;

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var fileName = $"quest_capture_{stamp}.png";
            var remotePath = $"/sdcard/Download/{fileName}";
            var localDir = EnsureMediaOutputDirectory(Environment.SpecialFolder.MyPictures);
            var localPath = Path.Combine(localDir, fileName);

            var capture = await RunAdb($"shell screencap -p {remotePath}");
            if (capture.ExitCode != 0)
            {
                ShowNotice("截图失败，请查看日志。", "warn");
                AddOperationHistory("屏幕", "截图", fileName, "失败");
                return;
            }

            var pull = await RunAdb($"pull {remotePath} \"{localPath}\"");
            await RunAdb($"shell rm {remotePath}", logOutput: false);
            if (pull.ExitCode != 0)
            {
                ShowNotice("截图已生成但拉取失败，请检查存储权限。", "warn");
                AddOperationHistory("屏幕", "截图", fileName, "拉取失败");
                return;
            }

            ShowNotice($"截图已保存到：{localPath}", "info");
            AddOperationHistory("屏幕", "截图", fileName, "成功");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ExpStartRecord_Click(object sender, RoutedEventArgs e)
    {
        if (_screenRecordRemotePath != null)
        {
            ShowNotice("已有录屏会话，请先停止并保存。", "warn");
            return;
        }

        SetBusy(true, "正在启动录屏...");
        try
        {
            if (!await EnsureExperimentReadyAsync()) return;

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _screenRecordFileName = $"quest_record_{stamp}.mp4";
            _screenRecordRemotePath = $"/sdcard/Download/{_screenRecordFileName}";

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = AdbPath,
                    Arguments = $"shell screenrecord --time-limit 180 {_screenRecordRemotePath}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };
            process.Exited += (_, __) =>
            {
                Dispatcher.Invoke(UpdateScreenRecordStateUi);
            };

            if (!process.Start())
            {
                _screenRecordRemotePath = null;
                _screenRecordFileName = null;
                ShowNotice("录屏进程启动失败。", "error");
                return;
            }

            _screenRecordProcess = process;
            AddOperationHistory("屏幕", "开始录屏", _screenRecordFileName ?? "-", "成功");
            ShowNotice("录屏已开始（最长 180 秒），完成后点“停止并保存”。", "info");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ExpStopRecord_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_screenRecordRemotePath) || string.IsNullOrWhiteSpace(_screenRecordFileName))
        {
            ShowNotice("当前没有可保存的录屏会话。", "warn");
            UpdateScreenRecordStateUi();
            return;
        }

        SetBusy(true, "正在停止录屏并拉取文件...");
        try
        {
            if (!await EnsureExperimentReadyAsync()) return;

            if (_screenRecordProcess != null && !_screenRecordProcess.HasExited)
            {
                try
                {
                    _screenRecordProcess.Kill();
                    await _screenRecordProcess.WaitForExitAsync();
                }
                catch { }
            }

            var localDir = EnsureMediaOutputDirectory(Environment.SpecialFolder.MyVideos);
            var localPath = Path.Combine(localDir, _screenRecordFileName);
            var pull = await RunAdb($"pull {_screenRecordRemotePath} \"{localPath}\"");
            await RunAdb($"shell rm {_screenRecordRemotePath}", logOutput: false);

            if (pull.ExitCode == 0)
            {
                ShowNotice($"录屏已保存到：{localPath}", "info");
                AddOperationHistory("屏幕", "停止录屏", _screenRecordFileName, "成功");
            }
            else
            {
                ShowNotice("录屏拉取失败，请查看日志。", "warn");
                AddOperationHistory("屏幕", "停止录屏", _screenRecordFileName, "失败");
            }
        }
        finally
        {
            _screenRecordProcess?.Dispose();
            _screenRecordProcess = null;
            _screenRecordRemotePath = null;
            _screenRecordFileName = null;
            SetBusy(false);
        }
    }

    private void ExpFillAutomationTemplate_Click(object sender, RoutedEventArgs e)
    {
        ExpAutomationScriptBox.Text = BuildDefaultAutomationTemplate();
        ShowNotice("已填充通用自动化模板。", "info");
    }

    private async void ExpRunAutomation_Click(object sender, RoutedEventArgs e)
    {
        var script = ExpAutomationScriptBox.Text ?? "";
        var lines = script.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x) && !x.StartsWith("#", StringComparison.Ordinal))
            .ToList();

        if (lines.Count == 0)
        {
            ShowNotice("脚本为空，请至少填写一条命令。", "warn");
            return;
        }

        SetBusy(true, $"正在执行自动化脚本（{lines.Count} 条）...");
        try
        {
            if (!await EnsureExperimentReadyAsync()) return;

            var successCount = 0;
            for (var i = 0; i < lines.Count; i++)
            {
                var rawLine = lines[i];
                var args = NormalizeAutomationAdbArgs(rawLine);
                var result = await RunAdb(args);
                if (result.ExitCode != 0)
                {
                    AddOperationHistory("自动化", "执行脚本", $"第 {i + 1} 条", "失败");
                    ShowNotice($"脚本在第 {i + 1} 条失败：{rawLine}", "warn");
                    return;
                }
                successCount++;
            }

            AddOperationHistory("自动化", "执行脚本", $"{successCount} 条命令", "成功");
            ShowNotice($"自动化脚本执行完成（成功 {successCount} 条）。", "info");
            await ReadExperimentalDisplayInfoAsync(showNotice: false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ApplyRefreshRateAsync(double hz)
    {
        SetBusy(true, "正在应用刷新率...");
        try
        {
            if (!await EnsureExperimentReadyAsync()) return;

            var rate = hz.ToString("0.##");
            await RunAdb($"shell settings put system peak_refresh_rate {rate}");
            await RunAdb($"shell settings put system min_refresh_rate {rate}");
            await RunAdb($"shell settings put system user_refresh_rate {rate}");

            ShowNotice($"已应用刷新率：{rate}Hz", "info");
            await ReadExperimentalDisplayInfoAsync(showNotice: false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<bool> EnsureExperimentReadyAsync()
    {
        if (!EnsureAdbExists()) return false;
        var state = await GetDeviceState();
        return GuardDeviceState(state);
    }

    private async Task ReadExperimentalDisplayInfoAsync(bool showNotice)
    {
        SetBusy(true, "正在读取显示参数...");
        try
        {
            if (!await EnsureExperimentReadyAsync()) return;

            var sizeResult = await RunAdb("shell wm size", logCommand: false);
            var peakResult = await RunAdb("shell settings get system peak_refresh_rate", logCommand: false);
            var minResult = await RunAdb("shell settings get system min_refresh_rate", logCommand: false);
            var userResult = await RunAdb("shell settings get system user_refresh_rate", logCommand: false);

            var sizeText = ParseWmSizeForDisplay(sizeResult.StdOut);
            var peak = NormalizeRefreshValue(FirstLineOrDefault(peakResult.StdOut, "-"));
            var min = NormalizeRefreshValue(FirstLineOrDefault(minResult.StdOut, "-"));
            var user = NormalizeRefreshValue(FirstLineOrDefault(userResult.StdOut, "-"));

            ExpDisplayInfoText.Text = $"分辨率：{sizeText}\n刷新率：peak={peak} / min={min} / user={user}";

            var sizeMatch = Regex.Match(sizeResult.StdOut ?? "", @"(Override size|Physical size):\s*(\d+)x(\d+)", RegexOptions.IgnoreCase);
            if (sizeMatch.Success)
            {
                ExpResolutionWidthBox.Text = sizeMatch.Groups[2].Value;
                ExpResolutionHeightBox.Text = sizeMatch.Groups[3].Value;
            }

            var userRate = NormalizeRefreshValue(FirstLineOrDefault(userResult.StdOut, "").Trim());
            if (!string.IsNullOrWhiteSpace(userRate) && !string.Equals(userRate, "系统默认", StringComparison.OrdinalIgnoreCase))
            {
                ExpRefreshRateBox.Text = userRate;
            }

            if (showNotice) ShowNotice("已读取当前显示参数。", "info");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string ParseWmSizeForDisplay(string output)
    {
        var overrideMatch = Regex.Match(output ?? "", @"Override size:\s*(\d+x\d+)", RegexOptions.IgnoreCase);
        if (overrideMatch.Success) return $"{overrideMatch.Groups[1].Value}（已覆盖）";

        var physicalMatch = Regex.Match(output ?? "", @"Physical size:\s*(\d+x\d+)", RegexOptions.IgnoreCase);
        if (physicalMatch.Success) return physicalMatch.Groups[1].Value;

        return "-";
    }

    private static (int Width, int Height)? TryParsePhysicalSize(string output)
    {
        var match = Regex.Match(output ?? "", @"Physical size:\s*(\d+)x(\d+)", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups[1].Value, out var width)) return null;
        if (!int.TryParse(match.Groups[2].Value, out var height)) return null;
        return (width, height);
    }

    private static string NormalizeRefreshValue(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text) || text == "-" || string.Equals(text, "null", StringComparison.OrdinalIgnoreCase))
        {
            return "系统默认";
        }
        return text;
    }

    private static string NormalizeAutomationAdbArgs(string rawLine)
    {
        var line = rawLine.Trim();
        if (line.StartsWith("adb ", StringComparison.OrdinalIgnoreCase))
        {
            return line[4..].Trim();
        }

        if (line.StartsWith("shell ", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("pull ", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("push ", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("install ", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("uninstall ", StringComparison.OrdinalIgnoreCase))
        {
            return line;
        }

        return $"shell {line}";
    }

    private static string BuildDefaultAutomationTemplate()
        => "# 通用模板\ngetprop ro.product.model\ngetprop ro.build.version.release\ndumpsys battery\ninput keyevent KEYCODE_HOME";

    private static string EnsureMediaOutputDirectory(Environment.SpecialFolder folder)
    {
        var root = Environment.GetFolderPath(folder);
        var path = Path.Combine(root, "QuestADBTool");
        Directory.CreateDirectory(path);
        return path;
    }

    private void UpdateScreenRecordStateUi()
    {
        var hasSession = !string.IsNullOrWhiteSpace(_screenRecordRemotePath);
        var running = _screenRecordProcess != null && !_screenRecordProcess.HasExited;

        if (ExpCaptureScreenButton != null) ExpCaptureScreenButton.IsEnabled = !_isBusy;
        if (ExpStartRecordButton != null) ExpStartRecordButton.IsEnabled = !_isBusy && !hasSession;
        if (ExpStopRecordButton != null) ExpStopRecordButton.IsEnabled = !_isBusy && hasSession;
        if (ExpRecordStateText != null)
        {
            ExpRecordStateText.Text = !hasSession
                ? "录屏状态：未开始"
                : (running ? "录屏状态：录制中（最长 180 秒）" : "录屏状态：录制结束，等待保存");
        }
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


    private void DeviceInfoScrollLeft_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceInfoScrollViewer == null) return;
        var target = Math.Max(0, DeviceInfoScrollViewer.HorizontalOffset - 220);
        DeviceInfoScrollViewer.ScrollToHorizontalOffset(target);
    }

    private void DeviceInfoScrollRight_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceInfoScrollViewer == null) return;
        var target = DeviceInfoScrollViewer.HorizontalOffset + 220;
        DeviceInfoScrollViewer.ScrollToHorizontalOffset(target);
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_logFilePath)) return;
        var dir = Path.GetDirectoryName(_logFilePath);
        if (string.IsNullOrWhiteSpace(dir)) return;
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }


    private async Task RefreshStatus()
    {
        if (!EnsureAdbExists())
        {
            StatusText.Text = "缺少 ADB 组件，请将 adb.exe 和两个 dll 放到程序目录的 adb 文件夹。";
            StatusHintText.Text = "请确认发布包内包含 adb 目录。";
            SetActionEnabled(false);
            ResetDeviceInfo();
            return;
        }

        var state = await GetDeviceState();
        switch (state)
        {
            case DeviceState.NotFound:
                StatusText.Text = "未检测到 Quest，请连接并开启开发者模式。";
                StatusHintText.Text = "检查线材与 USB 口，头显需已开机。";
                SetStatusDot("bad");
                SetActionEnabled(false);
                ResetDeviceInfo();
                break;
            case DeviceState.Unauthorized:
                StatusText.Text = "需要授权，请在头显内允许 USB 调试。";
                StatusHintText.Text = "戴上头显，勾选“始终允许来自此计算机”。";
                SetStatusDot("warn");
                SetActionEnabled(false);
                ResetDeviceInfo();
                break;
            case DeviceState.Connected:
                StatusText.Text = "已连接，可以开始使用。";
                StatusHintText.Text = "建议优先使用主板 USB 2.0 Type-A 口以提升稳定性。";
                SetStatusDot("ok");
                SetActionEnabled(true);
                await RefreshDeviceInfo();
                break;
            default:
                StatusText.Text = "状态未知。";
                StatusHintText.Text = "可尝试“修复连接”后重新检测。";
                SetStatusDot("idle");
                SetActionEnabled(false);
                ResetDeviceInfo();
                break;
        }
    }

    private async Task RefreshDeviceInfo()
    {
        var serial = await GetConnectedSerialAsync();
        if (string.IsNullOrWhiteSpace(serial))
        {
            ResetDeviceInfo();
            return;
        }

        var modelResult = await RunAdb($"-s {serial} shell getprop ro.product.model", logCommand: false);
        var androidResult = await RunAdb($"-s {serial} shell getprop ro.build.version.release", logCommand: false);
        var batteryResult = await RunAdb($"-s {serial} shell dumpsys battery", logCommand: false);
        var storageResult = await RunAdb($"-s {serial} shell df -h /data", logCommand: false);

        DeviceSerialText.Text = $"设备序列号：{serial}";
        DeviceModelText.Text = $"设备型号：{FirstLineOrDefault(modelResult.StdOut, "-")}";
        DeviceAndroidText.Text = $"Android：{FirstLineOrDefault(androidResult.StdOut, "-")}";
        DeviceBatteryText.Text = $"电量：{ParseBatteryLevel(batteryResult.StdOut)}";
        DeviceStorageText.Text = $"存储：{ParseStorage(storageResult.StdOut)}";
    }

    private void ResetDeviceInfo()
    {
        DeviceSerialText.Text = "设备序列号：-";
        DeviceModelText.Text = "设备型号：-";
        DeviceAndroidText.Text = "Android：-";
        DeviceBatteryText.Text = "电量：-";
        DeviceStorageText.Text = "存储：-";
    }

    private static string ParseBatteryLevel(string output)
    {
        var match = Regex.Match(output ?? "", @"level:\s*(\d+)", RegexOptions.IgnoreCase);
        return match.Success ? $"{match.Groups[1].Value}%" : "-";
    }

    private static string ParseStorage(string output)
    {
        var lines = (output ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return "-";
        var cols = Regex.Split(lines[^1].Trim(), @"\s+");
        if (cols.Length < 5) return lines[^1].Trim();
        return $"可用 {cols[3]} / 总计 {cols[1]}";
    }

    private static string FirstLineOrDefault(string output, string fallback)
    {
        var line = (output ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(line) ? fallback : line.Trim();
    }

    private void SetActionEnabled(bool enabled)
    {
        _isDeviceReady = enabled;
        UpdateQueueActionButtons();
        UpdateAppManageActionButtons();
        if (SendTextButton != null) SendTextButton.IsEnabled = enabled && !_isBusy;
    }

    private bool GuardDeviceState(DeviceState state)
    {
        if (state == DeviceState.NotFound)
        {
            ShowNotice("未检测到 Quest，请确认数据线和开发者模式。", "warn");
            return false;
        }
        if (state == DeviceState.Unauthorized)
        {
            ShowNotice("请在头显内确认 USB 调试授权后重试。", "warn");
            return false;
        }
        return true;
    }

    private bool EnsureAdbExists()
    {
        if (File.Exists(AdbPath)) return true;
        AppendLog("[错误] 未在 ./adb 找到 adb.exe");
        MessageBox.Show("未找到 adb.exe。\n\n请把以下文件放到程序目录的 adb 文件夹：\n- adb.exe\n- AdbWinApi.dll\n- AdbWinUsbApi.dll", "缺少 ADB 组件", MessageBoxButton.OK, MessageBoxImage.Error);
        return false;
    }

    private async Task<string?> GetConnectedSerialAsync()
    {
        var result = await RunAdb("devices", logCommand: false);
        var lines = result.StdOut.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.EndsWith("\tdevice", StringComparison.OrdinalIgnoreCase)) return line.Split('\t')[0].Trim();
        }
        return null;
    }

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

        if (result.StdErr.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)) return DeviceState.Unauthorized;
        if (result.StdErr.Contains("error", StringComparison.OrdinalIgnoreCase)) return DeviceState.Unknown;
        return DeviceState.NotFound;
    }

    private static string EncodeForAdbInputText(string s)
    {
        var sb = new StringBuilder(s.Length * 2);
        foreach (var ch in s)
        {
            if (ch == ' ') { sb.Append("%s"); continue; }
            if ("&|<>();".IndexOf(ch) >= 0 || ch is '"' or '\\') { sb.Append('_'); continue; }
            if (ch is '\r' or '\n') continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

}
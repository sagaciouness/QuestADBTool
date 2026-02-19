using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace QuestADBTool;

public partial class MainWindow : Window
{
    private sealed class InstallQueueItem : INotifyPropertyChanged
    {
        private string _status = QueuePending;
        private int _attemptCount;
        private TimeSpan _lastElapsed;
        private string _lastReason = "-";
        private string _lastAdvice = "-";

        public InstallQueueItem(string filePath)
        {
            FilePath = filePath;
            FileName = Path.GetFileName(filePath);
        }

        public string FilePath { get; }
        public string FileName { get; }

        public string Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusDisplay));
            }
        }

        public string StatusDisplay => Status switch
        {
            QueuePending => "等待中",
            QueueInstalling => "安装中",
            QueueSuccess => "成功",
            QueueFailed => "失败",
            _ => "-"
        };

        public int AttemptCount
        {
            get => _attemptCount;
            private set
            {
                if (_attemptCount == value) return;
                _attemptCount = value;
                OnPropertyChanged();
            }
        }

        public TimeSpan LastElapsed
        {
            get => _lastElapsed;
            private set
            {
                if (_lastElapsed == value) return;
                _lastElapsed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LastElapsedDisplay));
            }
        }

        public string LastElapsedDisplay => LastElapsed.TotalSeconds < 1 ? "-" : LastElapsed.ToString(@"mm\:ss");

        public string LastReason
        {
            get => _lastReason;
            private set
            {
                if (_lastReason == value) return;
                _lastReason = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LastReasonShort));
            }
        }

        public string LastReasonShort => string.IsNullOrWhiteSpace(LastReason) ? "-" : LastReason;

        public string LastAdvice
        {
            get => _lastAdvice;
            private set
            {
                if (_lastAdvice == value) return;
                _lastAdvice = value;
                OnPropertyChanged();
            }
        }

        public void MarkAttemptStarted()
        {
            AttemptCount++;
            LastElapsed = TimeSpan.Zero;
            LastReason = "正在安装...";
        }

        public void MarkCompleted(bool success, TimeSpan elapsed, string reason, string advice)
        {
            LastElapsed = elapsed;
            LastReason = success ? "安装成功" : reason;
            LastAdvice = success ? "-" : advice;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed class UpdateManifest
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }
        [JsonPropertyName("title")]
        public string? Title { get; set; }
        [JsonPropertyName("notes")]
        public string? Notes { get; set; }
        [JsonPropertyName("published_at")]
        public string? PublishedAt { get; set; }
        [JsonPropertyName("downloads")]
        public UpdateDownloads? Downloads { get; set; }
    }

    private sealed class UpdateDownloads
    {
        [JsonPropertyName("github_url")]
        public string? GithubUrl { get; set; }
        [JsonPropertyName("baidu_url")]
        public string? BaiduUrl { get; set; }
        [JsonPropertyName("baidu_code")]
        public string? BaiduCode { get; set; }
    }

    private enum DeviceState { Connected, Unauthorized, NotFound, Unknown }

    private const string QueuePending = "pending";
    private const string QueueInstalling = "installing";
    private const string QueueSuccess = "success";
    private const string QueueFailed = "failed";

    private string AdbPath => Path.Combine(AppContext.BaseDirectory, "adb", "adb.exe");

    private readonly object _logLock = new();
    private readonly ObservableCollection<InstallQueueItem> _installQueue = new();

    private bool _isBusy;
    private bool _isQueueRunning;
    private bool _isDeviceReady;
    private bool _isLogExpanded;
    private bool _isGuideDrawerAnimating;
    private ResponsiveTier _responsiveTier = ResponsiveTier.Wide;
    private UpdateManifest? _latestUpdate;

    private DispatcherTimer? _installProgressTimer;
    private DispatcherTimer? _noticeTimer;
    private DateTime _installStartAt;

    private Brush? _installCardDefaultBorderBrush;
    private Thickness _installCardDefaultBorderThickness;
    private Brush? _installCardDefaultBackground;

    private int _installSuccessCount;
    private int _installFailCount;

    private string _logFilePath = "";
    private static readonly HttpClient UpdateHttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private const string UpdateManifestUrl = "https://superpixel-1302573006.cos.ap-nanjing.myqcloud.com/update.json";

    private enum ResponsiveTier
    {
        Wide,
        Medium,
        Compact
    }

    public MainWindow()
    {
        InitializeComponent();

        _installCardDefaultBorderBrush = InstallCardBorder.BorderBrush;
        _installCardDefaultBorderThickness = InstallCardBorder.BorderThickness;
        _installCardDefaultBackground = InstallCardBorder.Background;

        QueueListBox.ItemsSource = _installQueue;
        _installProgressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _installProgressTimer.Tick += (_, __) =>
        {
            if (InstallBusyOverlay.Visibility == Visibility.Visible)
            {
                var elapsed = DateTime.Now - _installStartAt;
                InstallOverlayElapsedText.Text = $"已耗时 {elapsed:mm\\:ss}";
            }
        };
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _noticeTimer.Tick += (_, __) =>
        {
            _noticeTimer.Stop();
            NoticeBar.Visibility = Visibility.Collapsed;
        };

        UpdateInstallStats();
        UpdateQueueStats();
        ResetApkPreview();
        ResetDeviceInfo();
        SetStatusDot("idle");
        UpdateLogPanelState();
        InitLogFile();
        ShowFirstRunTip();
        ApplyResponsiveLayout();

        Loaded += async (_, __) =>
        {
            await RefreshStatus();
            await CheckForUpdatesAsync(userInitiated: false);
        };
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout();

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

    private void Guide_Click(object sender, RoutedEventArgs e) => ToggleGuideDrawer();

    private void GuideDrawerClose_Click(object sender, RoutedEventArgs e) => SetGuideDrawerVisible(false);

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(userInitiated: true);

    private void UpdateGithub_Click(object sender, RoutedEventArgs e) => OpenUrlSafe(_latestUpdate?.Downloads?.GithubUrl);

    private void UpdateBaidu_Click(object sender, RoutedEventArgs e) => OpenUrlSafe(_latestUpdate?.Downloads?.BaiduUrl);

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

    private void ApkPathBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateApkPreview(ApkPathBox.Text.Trim());
        UpdateQueueActionButtons();
    }

    private async void PickApk_Click(object sender, RoutedEventArgs e) => await PickAndEnqueueApksAsync();
    private async void AddQueueButton_Click(object sender, RoutedEventArgs e) => await PickAndEnqueueApksAsync();

    private void InstallApk_Click(object sender, RoutedEventArgs e)
    {
        var path = ApkPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowNotice("请先选择一个有效的 .apk 文件。", "warn");
            return;
        }

        if (!ValidateApkPath(path))
        {
            ShowNotice("APK 路径无效，请重新选择有效的 .apk 文件。", "warn");
            return;
        }

        EnqueueApks(new[] { path });
        ShowNotice("已加入安装队列，点击“开始安装”即可执行。", "info");
    }

    private async void StartQueueButton_Click(object sender, RoutedEventArgs e) => await ProcessInstallQueueAsync();

    private void ClearQueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isQueueRunning)
        {
            ShowNotice("当前正在安装，暂时不能清空队列。", "info");
            return;
        }
        _installQueue.Clear();
        UpdateQueueStats();
        ShowNotice("队列已清空。", "info");
    }

    private void RetryFailedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isQueueRunning) return;
        var changed = false;
        foreach (var item in _installQueue.Where(x => x.Status == QueueFailed))
        {
            item.Status = QueuePending;
            changed = true;
        }
        if (!changed)
        {
            ShowNotice("当前没有失败项可重试。", "info");
            return;
        }
        UpdateQueueStats();
        ShowNotice("已将失败项重置为待安装。", "info");
    }

    private void RemoveSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isQueueRunning) return;
        if (QueueListBox.SelectedItem is not InstallQueueItem selected)
        {
            UpdateQueueActionButtons();
            return;
        }
        _installQueue.Remove(selected);
        UpdateQueueStats();
    }

    private void QueueListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => UpdateQueueActionButtons();

    private void ToggleLogCollapse_Click(object sender, RoutedEventArgs e)
    {
        _isLogExpanded = !_isLogExpanded;
        UpdateLogPanelState();
    }

    private void UpdateLogPanelState()
    {
        if (LogBodyPanel == null || ToggleLogButton == null) return;
        LogBodyPanel.Visibility = _isLogExpanded ? Visibility.Visible : Visibility.Collapsed;
        ToggleLogButton.Content = _isLogExpanded ? "收起日志" : "展开日志";
    }

    private void InstallArea_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var valid = TryGetApkPathsFromDropData(e.Data, out _);
        e.Effects = valid ? DragDropEffects.Copy : DragDropEffects.None;
        SetInstallDropVisual(true, valid);
    }

    private void InstallArea_DragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        SetInstallDropVisual(false, true);
    }

    private void InstallArea_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        SetInstallDropVisual(false, true);
        if (!TryGetApkPathsFromDropData(e.Data, out var apkPaths))
        {
            ShowNotice("请拖入至少一个 .apk 文件。", "warn");
            return;
        }
        EnqueueApks(apkPaths);
        ShowNotice($"已加入 {apkPaths.Count} 个 APK 到安装队列。", "info");
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
                ShowNotice("请先输入要发送的文字。", "warn");
                InputTextBox.Focus();
                return;
            }
            var state = await GetDeviceState();
            if (!GuardDeviceState(state)) return;
            await RunAdb($"shell input text {EncodeForAdbInputText(text)}");
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
            OpenUrlSafe(AuthorUrl);
        }
        catch (Exception ex)
        {
            AppendLog($"[OpenAuthor] 打开作者链接失败: {ex.Message}");
            ShowNotice("打开作者链接失败，请检查系统默认浏览器设置。", "error", autoHide: false);
        }
    }

    private async Task PickAndEnqueueApksAsync()
    {
        var ofd = new OpenFileDialog
        {
            Filter = "APK Files (*.apk)|*.apk|All Files (*.*)|*.*",
            Title = "选择 APK（可多选）",
            Multiselect = true
        };
        if (ofd.ShowDialog() != true) return;
        EnqueueApks(ofd.FileNames);
        await Task.CompletedTask;
    }

    private void EnqueueApks(IEnumerable<string> paths)
    {
        var validPaths = paths.Where(ValidateApkPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (validPaths.Count == 0) return;

        foreach (var path in validPaths)
        {
            if (_installQueue.Any(x => x.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
            _installQueue.Add(new InstallQueueItem(path));
        }

        var first = validPaths[0];
        ApkPathBox.Text = first;
        UpdateApkPreview(first);
        UpdateQueueStats();
    }

    private bool ValidateApkPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        if (!filePath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) return false;
        return File.Exists(filePath);
    }

    private static bool TryGetApkPathsFromDropData(IDataObject data, out List<string> apkPaths)
    {
        apkPaths = new List<string>();
        if (!data.GetDataPresent(DataFormats.FileDrop)) return false;
        if (data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return false;

        apkPaths = files.Where(p => !string.IsNullOrWhiteSpace(p))
            .Where(p => p.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return apkPaths.Count > 0;
    }

    private async Task ProcessInstallQueueAsync()
    {
        if (_isQueueRunning) return;
        if (!EnsureAdbExists()) return;

        var hasPending = _installQueue.Any(x => x.Status is QueuePending or QueueFailed);
        if (!hasPending)
        {
            ShowNotice("队列中没有可安装项。", "info");
            return;
        }

        _isQueueRunning = true;
        SetBusy(true, "正在处理安装队列...");
        StartInstallProgress();
        UpdateQueueStats();

        try
        {
            foreach (var item in _installQueue.Where(x => x.Status is QueuePending or QueueFailed).ToList())
            {
                item.Status = QueueInstalling;
                item.MarkAttemptStarted();
                UpdateQueueStats();

                var installed = await InstallSingleAsync(item);
                item.Status = installed ? QueueSuccess : QueueFailed;
                UpdateQueueStats();
            }
        }
        finally
        {
            StopInstallProgress();
            _isQueueRunning = false;
            SetBusy(false);
            UpdateQueueStats();
            await RefreshStatus();
        }
    }

    private string BuildInstallArgs(string apkPath)
    {
        var flags = new List<string>();
        if (ReplaceInstallCheckBox.IsChecked == true) flags.Add("-r");
        if (AllowDowngradeCheckBox.IsChecked == true) flags.Add("-d");
        if (AllowTestApkCheckBox.IsChecked == true) flags.Add("-t");
        return $"install {string.Join(" ", flags)} \"{apkPath}\"".Replace("  ", " ").Trim();
    }

    private async Task<bool> InstallSingleAsync(InstallQueueItem item)
    {
        var apkPath = item.FilePath;
        var startedAt = DateTime.Now;

        if (!ValidateApkPath(apkPath))
        {
            AppendLog($"[Install] 文件无效: {apkPath}");
            item.MarkCompleted(false, DateTime.Now - startedAt, "文件无效", "请重新选择有效的 .apk 文件。");
            _installFailCount++;
            UpdateInstallStats();
            return false;
        }

        var state = await GetDeviceState();
        if (!GuardDeviceState(state))
        {
            item.MarkCompleted(false, DateTime.Now - startedAt, "设备不可用", "请先完成连接和授权后再重试。");
            _installFailCount++;
            UpdateInstallStats();
            return false;
        }

        var result = await RunAdb(BuildInstallArgs(apkPath));
        var combined = $"{result.StdOut}\n{result.StdErr}";
        var success = result.ExitCode == 0 && combined.Contains("Success", StringComparison.OrdinalIgnoreCase);

        if (success)
        {
            item.MarkCompleted(true, DateTime.Now - startedAt, "安装成功", "-");
            _installSuccessCount++;
        }
        else
        {
            _installFailCount++;
            var reason = ExtractInstallFailureReason(combined);
            var advice = ExtractInstallFailureAdvice(combined);
            item.MarkCompleted(false, DateTime.Now - startedAt, reason, advice);
            AppendLog($"[Install] 失败原因: {reason}");
            AppendLog($"[Install] 建议处理: {advice}");
        }

        UpdateInstallStats();
        return success;
    }

    private void UpdateInstallStats()
    {
        InstallSuccessBadge.Text = $"成功 {_installSuccessCount}";
        InstallFailBadge.Text = $"失败 {_installFailCount}";
    }

    private void UpdateQueueStats()
    {
        var pending = _installQueue.Count(x => x.Status == QueuePending);
        var running = _installQueue.Count(x => x.Status == QueueInstalling);
        var failed = _installQueue.Count(x => x.Status == QueueFailed);
        var done = _installQueue.Count(x => x.Status is QueueSuccess or QueueFailed);

        QueueCountText.Text = $"待处理：{pending} 进行中：{running} 已完成：{done}";

        if (StartQueueButton != null)
        {
            StartQueueButton.Content = _isQueueRunning ? "安装中..." : (pending > 0 || failed > 0 ? "开始安装" : "无待处理");
        }

        UpdateQueueActionButtons();
    }

    private void UpdateQueueActionButtons()
    {
        var hasFailed = _installQueue.Any(x => x.Status == QueueFailed);
        var hasPending = _installQueue.Any(x => x.Status is QueuePending or QueueFailed);
        var hasAny = _installQueue.Count > 0;
        var hasSelected = QueueListBox?.SelectedItem is InstallQueueItem;

        var canRun = !_isBusy && _isDeviceReady;
        var canEdit = !_isBusy && !_isQueueRunning;

        if (AddQueueButton != null) AddQueueButton.IsEnabled = canEdit;
        if (ClearQueueButton != null) ClearQueueButton.IsEnabled = canEdit && hasAny;
        if (RetryFailedButton != null) RetryFailedButton.IsEnabled = canEdit && hasFailed;
        if (RemoveSelectedButton != null) RemoveSelectedButton.IsEnabled = canEdit && hasSelected;
        if (StartQueueButton != null) StartQueueButton.IsEnabled = canRun && hasPending;
        if (InstallApkButton != null) InstallApkButton.IsEnabled = canEdit && ValidateApkPath(ApkPathBox.Text.Trim());
    }

    private void StartInstallProgress()
    {
        _installStartAt = DateTime.Now;
        InstallBusyOverlay.Visibility = Visibility.Visible;
        InstallOverlayProgressBar.IsIndeterminate = true;
        InstallOverlayText.Text = "正在处理安装队列...";
        InstallOverlayElapsedText.Text = "已耗时 00:00";
        _installProgressTimer?.Start();
    }

    private void StopInstallProgress()
    {
        _installProgressTimer?.Stop();
        InstallBusyOverlay.Visibility = Visibility.Collapsed;
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

    private static string ExtractInstallFailureReason(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "未收到 adb 输出。";

        var failureMatch = Regex.Match(output, @"Failure\s*\[(?<reason>[^\]]+)\]", RegexOptions.IgnoreCase);
        if (failureMatch.Success) return failureMatch.Groups["reason"].Value;

        var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 ? lines[0].Trim() : "安装失败，未获取到具体错误信息。";
    }

    private static string ExtractInstallFailureAdvice(string output)
    {
        var upper = output.ToUpperInvariant();
        if (upper.Contains("INSTALL_FAILED_VERSION_DOWNGRADE")) return "设备上已有更高版本，可勾选“允许降级”或先卸载后安装。";
        if (upper.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE")) return "签名不一致，请先卸载旧版本再安装。";
        if (upper.Contains("INSTALL_FAILED_ALREADY_EXISTS")) return "设备已有同包名应用，可先卸载再安装。";
        if (upper.Contains("INSTALL_PARSE_FAILED")) return "APK 可能损坏，建议重新下载。";
        if (upper.Contains("INSTALL_FAILED_INSUFFICIENT_STORAGE")) return "设备存储不足，请先清理空间。";
        if (upper.Contains("INSTALL_FAILED_TEST_ONLY")) return "此 APK 为 testOnly，请勾选“允许测试包”。";
        if (upper.Contains("INSTALL_FAILED_OLDER_SDK")) return "设备系统版本过低，不满足 APK 要求。";
        if (upper.Contains("INSTALL_FAILED_NO_MATCHING_ABIS")) return "APK 架构与设备不匹配。";
        if (upper.Contains("OFFLINE") || upper.Contains("NO DEVICES/EMULATORS FOUND") || upper.Contains("EOF")) return "ADB 连接不稳定，优先使用主板 USB 2.0 口并重插数据线。";
        return "请查看完整日志并确认设备授权、连接稳定性与 APK 来源。";
    }

    private void UpdateApkPreview(string apkPath)
    {
        if (!ValidateApkPath(apkPath))
        {
            ResetApkPreview();
            return;
        }
        var fi = new FileInfo(apkPath);
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

    private void ShowFirstRunTip()
    {
        var flagPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuestADBTool", "first_run.flag");
        if (File.Exists(flagPath)) return;
        SetGuideDrawerVisible(true);
        ShowNotice("首次使用已为你打开新手引导，可随时通过右上角“新手引导”再次查看。", "info");

        var dir = Path.GetDirectoryName(flagPath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(flagPath, "ok");
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

    private void InitLogFile()
    {
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuestADBTool", "logs");
        Directory.CreateDirectory(logDir);
        _logFilePath = Path.Combine(logDir, $"log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

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
        lock (_logLock) { File.AppendAllText(_logFilePath, content, Encoding.UTF8); }
    }

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (string.IsNullOrWhiteSpace(UpdateManifestUrl) || UpdateManifestUrl.Contains("your-cos-domain", StringComparison.OrdinalIgnoreCase))
        {
            if (userInitiated) ShowNotice("请先在代码中配置 COS 的 update.json 地址。", "warn");
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UpdateManifestUrl);
            request.Headers.UserAgent.ParseAdd("QuestADBTool/1.0");

            var response = await UpdateHttpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, options);

            if (manifest?.Downloads == null || string.IsNullOrWhiteSpace(manifest.Version))
            {
                if (userInitiated) ShowNotice("更新清单格式无效。", "warn");
                return;
            }

            var currentVersion = GetCurrentVersionText();
            if (!IsNewerVersion(manifest.Version, currentVersion))
            {
                UpdatePanel.Visibility = Visibility.Collapsed;
                if (userInitiated) ShowNotice($"当前已是最新版本（v{currentVersion}）。", "info");
                return;
            }

            _latestUpdate = manifest;
            var versionTitle = string.IsNullOrWhiteSpace(manifest.Title) ? $"v{manifest.Version}" : $"{manifest.Title}（v{manifest.Version}）";
            UpdateVersionText.Text = versionTitle;
            UpdatePublishedText.Text = $"发布时间：{FormatPublishedAt(manifest.PublishedAt)}";
            UpdateNotesText.Text = string.IsNullOrWhiteSpace(manifest.Notes)
                ? "更新内容：暂无说明。"
                : $"更新内容：\n{manifest.Notes.Trim()}";

            UpdateGithubButton.IsEnabled = !string.IsNullOrWhiteSpace(manifest.Downloads.GithubUrl);
            UpdateBaiduButton.IsEnabled = !string.IsNullOrWhiteSpace(manifest.Downloads.BaiduUrl);
            UpdateBaiduButton.Content = string.IsNullOrWhiteSpace(manifest.Downloads.BaiduCode)
                ? "百度网盘下载"
                : $"百度网盘下载（提取码 {manifest.Downloads.BaiduCode.Trim()}）";

            UpdatePanel.Visibility = Visibility.Visible;
            if (!userInitiated) ShowNotice($"发现新版本 v{manifest.Version}，可选择 GitHub 或百度网盘下载。", "info");
        }
        catch (Exception ex)
        {
            AppendLog($"[Update] 检查更新失败: {ex.Message}");
            if (userInitiated) ShowNotice("检查更新失败，请稍后重试。", "warn");
        }
    }

    private static string GetCurrentVersionText()
    {
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var clean = info.Split('+')[0].Trim();
            if (!string.IsNullOrWhiteSpace(clean)) return NormalizeVersion(clean);
        }

        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        return ver == null ? "0.0.0" : $"{ver.Major}.{ver.Minor}.{Math.Max(ver.Build, 0)}";
    }

    private static bool IsNewerVersion(string latest, string current)
    {
        var latestNorm = NormalizeVersion(latest);
        var currentNorm = NormalizeVersion(current);

        if (Version.TryParse(latestNorm, out var latestVer) && Version.TryParse(currentNorm, out var currentVer))
        {
            return latestVer > currentVer;
        }

        return !string.Equals(latestNorm, currentNorm, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeVersion(string raw)
    {
        var text = (raw ?? "").Trim().TrimStart('v', 'V');
        var dashIndex = text.IndexOf('-');
        if (dashIndex >= 0) text = text[..dashIndex];
        return text;
    }

    private static string FormatPublishedAt(string? raw)
    {
        if (DateTimeOffset.TryParse(raw, out var dt))
        {
            return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
        return string.IsNullOrWhiteSpace(raw) ? "-" : raw;
    }

    private void OpenUrlSafe(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowNotice("当前未配置有效下载地址。", "warn");
            return;
        }
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    private void SetBusy(bool isBusy, string? message = null)
    {
        _isBusy = isBusy;

        if (SendTextButton != null) SendTextButton.IsEnabled = _isDeviceReady && !isBusy;
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
        if (AuthorButton != null) AuthorButton.IsEnabled = !isBusy;
        if (ToggleLogButton != null) ToggleLogButton.IsEnabled = !isBusy;

        if (BusyText != null)
        {
            BusyText.Text = message ?? "";
            BusyText.Visibility = (isBusy && !string.IsNullOrWhiteSpace(message)) ? Visibility.Visible : Visibility.Collapsed;
        }

        UpdateQueueActionButtons();
        Cursor = isBusy ? Cursors.Wait : Cursors.Arrow;
    }

    private void NoticeClose_Click(object sender, RoutedEventArgs e)
    {
        _noticeTimer?.Stop();
        NoticeBar.Visibility = Visibility.Collapsed;
    }

    private void ShowNotice(string message, string level = "info", bool autoHide = true)
    {
        if (NoticeBar == null || NoticeTagText == null || NoticeText == null) return;

        var style = level switch
        {
            "warn" => ("注意", "WarnBg", "WarnBorder", "WarnText"),
            "error" => ("错误", "ErrorBg", "ErrorBorder", "ErrorText"),
            _ => ("提示", "InfoBg", "InfoBorder", "InfoText")
        };

        NoticeTagText.Text = style.Item1;
        NoticeTagText.Foreground = ResolveBrush(style.Item4, "#1D4ED8");
        NoticeText.Text = message;
        NoticeBar.Background = ResolveBrush(style.Item2, "#EFF6FF");
        NoticeBar.BorderBrush = ResolveBrush(style.Item3, "#BFDBFE");
        NoticeBar.Visibility = Visibility.Visible;

        _noticeTimer?.Stop();
        if (autoHide) _noticeTimer?.Start();
    }

    private void ToggleGuideDrawer() => SetGuideDrawerVisible(GuideDrawer.Visibility != Visibility.Visible);

    private void SetGuideDrawerVisible(bool visible)
    {
        if (GuideDrawer == null || GuideDrawerTranslate == null) return;
        if (_isGuideDrawerAnimating) return;

        var isCurrentlyVisible = GuideDrawer.Visibility == Visibility.Visible;
        if (visible == isCurrentlyVisible && !visible) return;

        _isGuideDrawerAnimating = true;

        if (visible)
        {
            GuideDrawer.Visibility = Visibility.Visible;
            GuideDrawer.Opacity = 0;
            GuideDrawerTranslate.X = GetGuideDrawerHiddenOffset();
        }

        var slide = new DoubleAnimation
        {
            To = visible ? 0 : GetGuideDrawerHiddenOffset(),
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var fade = new DoubleAnimation
        {
            To = visible ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(180)
        };

        slide.Completed += (_, __) =>
        {
            if (!visible)
            {
                GuideDrawer.Visibility = Visibility.Collapsed;
            }
            _isGuideDrawerAnimating = false;
        };

        GuideDrawerTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slide);
        GuideDrawer.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private Brush ResolveBrush(string resourceKey, string fallbackHex)
    {
        var brush = TryFindResource(resourceKey) as Brush;
        if (brush != null) return brush;
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallbackHex));
    }

    private void ApplyResponsiveLayout()
    {
        if (RootLayoutGrid == null) return;

        var width = ActualWidth > 0 ? ActualWidth : Width;
        var nextTier = width switch
        {
            < 1080 => ResponsiveTier.Compact,
            < 1400 => ResponsiveTier.Medium,
            _ => ResponsiveTier.Wide
        };

        if (nextTier == _responsiveTier) return;
        _responsiveTier = nextTier;

        switch (_responsiveTier)
        {
            case ResponsiveTier.Compact:
                RootLayoutGrid.Margin = new Thickness(8);
                SetControlHeight(32, RefreshButton, RestartButton, GuideButton, OpenLogButton, CheckUpdateButton,
                    PickApkButton, InstallApkButton, SendTextButton, AddQueueButton, StartQueueButton,
                    ClearQueueButton, RetryFailedButton, RemoveSelectedButton, AuthorButton, ClearLogButton, CopyLogButton);
                SetControlHeight(32, ApkPathBox, InputTextBox);
                SetInstallColumns(1.0, 1.0);
                QueueListBox.Height = 96;
                LogBox.MaxHeight = 140;
                GuideDrawer.Width = 300;
                break;

            case ResponsiveTier.Medium:
                RootLayoutGrid.Margin = new Thickness(10);
                SetControlHeight(34, RefreshButton, RestartButton, GuideButton, OpenLogButton, CheckUpdateButton,
                    PickApkButton, InstallApkButton, SendTextButton, AddQueueButton, StartQueueButton,
                    ClearQueueButton, RetryFailedButton, RemoveSelectedButton, AuthorButton, ClearLogButton, CopyLogButton);
                SetControlHeight(34, ApkPathBox, InputTextBox);
                SetInstallColumns(1.15, 1.0);
                QueueListBox.Height = 108;
                LogBox.MaxHeight = 160;
                GuideDrawer.Width = 330;
                break;

            default:
                RootLayoutGrid.Margin = new Thickness(14);
                SetControlHeight(36, RefreshButton, RestartButton, GuideButton, OpenLogButton, CheckUpdateButton,
                    PickApkButton, InstallApkButton, SendTextButton, AddQueueButton, StartQueueButton,
                    ClearQueueButton, RetryFailedButton, RemoveSelectedButton, AuthorButton, ClearLogButton, CopyLogButton);
                SetControlHeight(36, ApkPathBox, InputTextBox);
                SetInstallColumns(1.35, 0.95);
                QueueListBox.Height = 116;
                LogBox.MaxHeight = 180;
                GuideDrawer.Width = 360;
                break;
        }

        if (GuideDrawerTranslate != null && GuideDrawer.Visibility != Visibility.Visible)
        {
            GuideDrawerTranslate.X = GetGuideDrawerHiddenOffset();
        }
    }

    private void SetInstallColumns(double left, double right)
    {
        if (InstallLeftColumn != null) InstallLeftColumn.Width = new GridLength(left, GridUnitType.Star);
        if (InstallRightColumn != null) InstallRightColumn.Width = new GridLength(right, GridUnitType.Star);
    }

    private static void SetControlHeight(double height, params FrameworkElement?[] elements)
    {
        foreach (var element in elements)
        {
            if (element != null) element.Height = height;
        }
    }

    private double GetGuideDrawerHiddenOffset()
    {
        var width = GuideDrawer?.Width ?? 360;
        return width + 20;
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


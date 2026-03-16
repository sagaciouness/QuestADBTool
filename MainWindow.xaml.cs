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
using QuestADBTool.Views;

namespace QuestADBTool;

public partial class MainWindow : Window
{
    private enum AppPage
    {
        Device,
        Install,
        Apps,
        Log,
        About
    }

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

    private sealed class ManagedApp
    {
        public required string PackageName { get; init; }
        public required string DisplayName { get; set; }
        public bool IsSystemApp { get; init; }
        public bool IsOfficialApp { get; init; }
        public string VersionName { get; set; } = "-";
        public string VersionCode { get; set; } = "-";
        public string CodePath { get; set; } = "-";
        public string FirstInstallTime { get; set; } = "-";
        public string LastUpdateTime { get; set; } = "-";
        public bool? IsLaunchable { get; set; }
    }

    private sealed class PackageDumpInfo
    {
        public string? DisplayName { get; set; }
        public string? VersionName { get; set; }
        public string? VersionCode { get; set; }
        public string? CodePath { get; set; }
        public string? FirstInstallTime { get; set; }
        public string? LastUpdateTime { get; set; }
    }

    private sealed class OperationHistoryItem
    {
        public required string TimeText { get; init; }
        public required string Category { get; init; }
        public required string Action { get; init; }
        public required string Target { get; init; }
        public required string Result { get; init; }
    }


    private sealed class AppSettings
    {
        [JsonPropertyName("dark_mode")]
        public bool DarkMode { get; set; }
    }
    private const string QueuePending = "pending";
    private const string QueueInstalling = "installing";
    private const string QueueSuccess = "success";
    private const string QueueFailed = "failed";

    private string AdbPath => Path.Combine(AppContext.BaseDirectory, "adb", "adb.exe");

    private readonly object _logLock = new();
    private readonly ObservableCollection<InstallQueueItem> _installQueue = new();
    private readonly ObservableCollection<ManagedApp> _managedApps = new();
    private readonly ObservableCollection<OperationHistoryItem> _operationHistory = new();
    private List<ManagedApp> _managedAppsAll = new();

    private bool _isBusy;
    private bool _isQueueRunning;
    private bool _isDeviceReady;
    private bool _isLogExpanded;
    private bool _isGuideDrawerAnimating;
    private bool _isNavPinned;
    private bool _isNavAnimating;
    private bool _navIntroPlayed;
    private bool _isDarkMode;
    private readonly DateTime _launchOverlayShownAt = DateTime.Now;
    private ResponsiveTier _responsiveTier = ResponsiveTier.Wide;
    private UpdateManifest? _latestUpdate;
    private Process? _screenRecordProcess;
    private string? _screenRecordRemotePath;
    private string? _screenRecordFileName;

    private DispatcherTimer? _installProgressTimer;
    private DispatcherTimer? _noticeTimer;
    private DispatcherTimer? _navHoverIntentTimer;
    private DateTime _installStartAt;
    private bool _navHoverExpandTarget;

    private Brush? _installCardDefaultBorderBrush;
    private Thickness _installCardDefaultBorderThickness;
    private Brush? _installCardDefaultBackground;

    private int _installSuccessCount;
    private int _installFailCount;

    private string _logFilePath = "";
    private string _settingsFilePath = "";
    private static readonly HttpClient UpdateHttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private const string UpdateManifestUrl = "https://superpixel-1302573006.cos.ap-nanjing.myqcloud.com/update.json";
    private const double NavCollapsedWidth = 56;
    private const double NavExpandedWidth = 195;
    private readonly bool _navKeepExpandedAfterIntro = false;

    private enum ResponsiveTier
    {
        Wide,
        Medium,
        Compact
    }

    private AppPage _activePage = AppPage.Device;

    private System.Windows.Shapes.Ellipse StatusDot => DevicePageHost.StatusDotControl;
    private System.Windows.Controls.TextBlock StatusText => DevicePageHost.StatusTextControl;
    private System.Windows.Controls.TextBlock StatusHintText => DevicePageHost.StatusHintTextControl;
    private System.Windows.Controls.Button RefreshButton => DevicePageHost.RefreshButtonControl;
    private System.Windows.Controls.Button RestartButton => DevicePageHost.RestartButtonControl;
    private System.Windows.Controls.Button GuideButton => DevicePageHost.GuideButtonControl;
    private System.Windows.Controls.Button OpenLogButton => DevicePageHost.OpenLogButtonControl;
    private System.Windows.Controls.Button CheckUpdateButton => DevicePageHost.CheckUpdateButtonControl;
    private System.Windows.Controls.ScrollViewer DeviceInfoScrollViewer => DevicePageHost.DeviceInfoScrollViewerControl;
    private System.Windows.Controls.Button DeviceInfoScrollLeftButton => DevicePageHost.DeviceInfoScrollLeftButtonControl;
    private System.Windows.Controls.Button DeviceInfoScrollRightButton => DevicePageHost.DeviceInfoScrollRightButtonControl;
    private System.Windows.Controls.TextBlock DeviceSerialText => DevicePageHost.DeviceSerialTextControl;
    private System.Windows.Controls.TextBlock DeviceModelText => DevicePageHost.DeviceModelTextControl;
    private System.Windows.Controls.TextBlock DeviceAndroidText => DevicePageHost.DeviceAndroidTextControl;
    private System.Windows.Controls.TextBlock DeviceBatteryText => DevicePageHost.DeviceBatteryTextControl;
    private System.Windows.Controls.TextBlock DeviceStorageText => DevicePageHost.DeviceStorageTextControl;
    private System.Windows.Controls.Button ExpCaptureScreenButton => DevicePageHost.ExpCaptureScreenButtonControl;
    private System.Windows.Controls.Button ExpStartRecordButton => DevicePageHost.ExpStartRecordButtonControl;
    private System.Windows.Controls.Button ExpStopRecordButton => DevicePageHost.ExpStopRecordButtonControl;
    private System.Windows.Controls.TextBlock ExpRecordStateText => DevicePageHost.ExpRecordStateTextControl;
    private System.Windows.Controls.TextBox ExpAutomationScriptBox => DevicePageHost.ExpAutomationScriptBoxControl;
    private System.Windows.Controls.Button ExpRunAutomationButton => DevicePageHost.ExpRunAutomationButtonControl;
    private System.Windows.Controls.Button ExpFillAutomationTemplateButton => DevicePageHost.ExpFillAutomationTemplateButtonControl;

    private System.Windows.Controls.Border InstallCardBorder => InstallPageHost.InstallCardBorderControl;
    private System.Windows.Controls.ColumnDefinition InstallLeftColumn => InstallPageHost.InstallLeftColumnControl;
    private System.Windows.Controls.ColumnDefinition InstallRightColumn => InstallPageHost.InstallRightColumnControl;
    private System.Windows.Controls.TextBlock InstallSuccessBadge => InstallPageHost.InstallSuccessBadgeControl;
    private System.Windows.Controls.TextBlock InstallFailBadge => InstallPageHost.InstallFailBadgeControl;
    private System.Windows.Shapes.Ellipse InstallDeviceStateDot => InstallPageHost.InstallDeviceStateDotControl;
    private System.Windows.Controls.TextBlock InstallDeviceStateText => InstallPageHost.InstallDeviceStateTextControl;
    private System.Windows.Controls.Button InstallHeaderRefreshButton => InstallPageHost.InstallHeaderRefreshButtonControl;
    private System.Windows.Controls.TextBox ApkPathBox => InstallPageHost.ApkPathBoxControl;
    private System.Windows.Controls.Button PickApkButton => InstallPageHost.PickApkButtonControl;
    private System.Windows.Controls.Button InstallApkButton => InstallPageHost.InstallApkButtonControl;
    private System.Windows.Controls.TextBox InputTextBox => InstallPageHost.InputTextBoxControl;
    private System.Windows.Controls.Button SendTextButton => InstallPageHost.SendTextButtonControl;
    private System.Windows.Controls.TextBlock ApkInfoFileText => InstallPageHost.ApkInfoFileTextControl;
    private System.Windows.Controls.TextBlock ApkInfoSizeText => InstallPageHost.ApkInfoSizeTextControl;
    private System.Windows.Controls.TextBlock ApkInfoPackageText => InstallPageHost.ApkInfoPackageTextControl;
    private System.Windows.Controls.TextBlock ApkInfoVersionText => InstallPageHost.ApkInfoVersionTextControl;
    private System.Windows.Controls.Expander AdvancedOptionsExpander => InstallPageHost.AdvancedOptionsExpanderControl;
    private System.Windows.Controls.CheckBox ReplaceInstallCheckBox => InstallPageHost.ReplaceInstallCheckBoxControl;
    private System.Windows.Controls.CheckBox AllowDowngradeCheckBox => InstallPageHost.AllowDowngradeCheckBoxControl;
    private System.Windows.Controls.CheckBox AllowTestApkCheckBox => InstallPageHost.AllowTestApkCheckBoxControl;
    private System.Windows.Controls.TextBlock QueueCountText => InstallPageHost.QueueCountTextControl;
    private System.Windows.Controls.ListBox QueueListBox => InstallPageHost.QueueListBoxControl;
    private System.Windows.Controls.Button AddQueueButton => InstallPageHost.AddQueueButtonControl;
    private System.Windows.Controls.Button StartQueueButton => InstallPageHost.StartQueueButtonControl;
    private System.Windows.Controls.Button ClearQueueButton => InstallPageHost.ClearQueueButtonControl;
    private System.Windows.Controls.Button RetryFailedButton => InstallPageHost.RetryFailedButtonControl;
    private System.Windows.Controls.Button RemoveSelectedButton => InstallPageHost.RemoveSelectedButtonControl;
    private System.Windows.Controls.Border InstallBusyOverlay => InstallPageHost.InstallBusyOverlayControl;
    private System.Windows.Controls.TextBlock InstallOverlayText => InstallPageHost.InstallOverlayTextControl;
    private System.Windows.Controls.ProgressBar InstallOverlayProgressBar => InstallPageHost.InstallOverlayProgressBarControl;
    private System.Windows.Controls.TextBlock InstallOverlayElapsedText => InstallPageHost.InstallOverlayElapsedTextControl;

    private System.Windows.Controls.Button AppRefreshButton => AppManagePageHost.AppRefreshButtonControl;
    private System.Windows.Controls.CheckBox AppIncludeSystemCheckBox => AppManagePageHost.AppIncludeSystemCheckBoxControl;
    private System.Windows.Controls.TextBox AppSearchBox => AppManagePageHost.AppSearchBoxControl;
    private System.Windows.Controls.TextBlock AppCountText => AppManagePageHost.AppCountTextControl;
    private System.Windows.Controls.StackPanel AppRefreshProgressPanel => AppManagePageHost.AppRefreshProgressPanelControl;
    private System.Windows.Controls.TextBlock AppRefreshProgressText => AppManagePageHost.AppRefreshProgressTextControl;
    private System.Windows.Controls.TextBlock AppRefreshProgressValueText => AppManagePageHost.AppRefreshProgressValueTextControl;
    private System.Windows.Controls.ProgressBar AppRefreshProgressBar => AppManagePageHost.AppRefreshProgressBarControl;
    private System.Windows.Controls.ListBox AppListBox => AppManagePageHost.AppListBoxControl;
    private System.Windows.Controls.TextBlock AppSelectedPackageText => AppManagePageHost.AppSelectedPackageTextControl;
    private System.Windows.Controls.Button AppLaunchButton => AppManagePageHost.AppLaunchButtonControl;
    private System.Windows.Controls.Button AppUninstallButton => AppManagePageHost.AppUninstallButtonControl;
    private System.Windows.Controls.Button AppCopyPackageButton => AppManagePageHost.AppCopyPackageButtonControl;

    private System.Windows.Controls.Button ToggleLogButton => LogPageHost.ToggleLogButtonControl;
    private System.Windows.Controls.StackPanel LogBodyPanel => LogPageHost.LogBodyPanelControl;
    private System.Windows.Controls.Button AuthorButton => LogPageHost.AuthorButtonControl;
    private System.Windows.Controls.Button ClearLogButton => LogPageHost.ClearLogButtonControl;
    private System.Windows.Controls.Button CopyLogButton => LogPageHost.CopyLogButtonControl;
    private System.Windows.Controls.Button ClearHistoryButton => LogPageHost.ClearHistoryButtonControl;
    private System.Windows.Controls.Button CopyHistoryButton => LogPageHost.CopyHistoryButtonControl;
    private System.Windows.Controls.ListBox OperationHistoryListBox => LogPageHost.OperationHistoryListBoxControl;
    private System.Windows.Controls.TextBox LogBox => LogPageHost.LogBoxControl;

    public MainWindow()
    {
        InitializeComponent();
        WirePageEvents();
        InitializeUsbOnlyExtensions();
        SwitchPage(AppPage.Device);
        SetNavRailVisualState(expanded: false);
        UpdateNavPinToggleVisual();
        UpdateWindowChromeButtons();

        _installCardDefaultBorderBrush = InstallCardBorder.BorderBrush;
        _installCardDefaultBorderThickness = InstallCardBorder.BorderThickness;
        _installCardDefaultBackground = InstallCardBorder.Background;

        QueueListBox.ItemsSource = _installQueue;
        AppListBox.ItemsSource = _managedApps;
        OperationHistoryListBox.ItemsSource = _operationHistory;
        AppSearchBox.Text = "";
        AppCountText.Text = "应用数：0";
        AppSelectedPackageText.Text = "包名：-";
        SetAppRefreshProgress(false, 0, 0, "");
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
        _navHoverIntentTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _navHoverIntentTimer.Tick += async (_, __) =>
        {
            _navHoverIntentTimer.Stop();
            if (_isNavPinned || _isNavAnimating) return;
            await AnimateNavRailAsync(expand: _navHoverExpandTarget, durationMs: _navHoverExpandTarget ? 380 : 300);
        };

        UpdateInstallStats();
        UpdateQueueStats();
        UpdateAppManageActionButtons();
        ResetApkPreview();
        ResetDeviceInfo();
        SetStatusDot("idle");
        UpdateLogPanelState();
        ExpAutomationScriptBox.Text = "";
        UpdateScreenRecordStateUi();
        InitLogFile();
        InitSettingsFile();
        ApplyTheme(LoadThemeSetting(), persist: false);
        ShowFirstRunTip();
        ApplyResponsiveLayout();

        Loaded += async (_, __) =>
        {
            try
            {
                LaunchStatusText.Text = "正在准备侧栏动画...";
                await PlayNavRailStartupAnimationAsync();
                LaunchStatusText.Text = "正在检测设备状态...";
                await RefreshStatus();
                LaunchStatusText.Text = "正在检查版本更新...";
                await CheckForUpdatesAsync(userInitiated: false);
            }
            finally
            {
                await DismissLaunchOverlayAsync();
            }
        };
        StateChanged += (_, __) => UpdateWindowChromeButtons();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }
        DragMove();
    }

    private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxRestoreButton_Click(object sender, RoutedEventArgs e) => ToggleWindowState();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateWindowChromeButtons();
    }

    private void UpdateWindowChromeButtons()
    {
        if (MaxRestoreButton == null) return;
        MaxRestoreButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private async Task PlayNavRailStartupAnimationAsync()
    {
        if (_navIntroPlayed) return;
        _navIntroPlayed = true;
        _isNavPinned = true;
        await AnimateNavRailAsync(expand: true, durationMs: 420);

        if (_navKeepExpandedAfterIntro)
        {
            _isNavPinned = true;
            UpdateNavPinToggleVisual();
            return;
        }

        _isNavPinned = false;
        UpdateNavPinToggleVisual();
        if (!NavRail.IsMouseOver)
        {
            await AnimateNavRailAsync(expand: false, durationMs: 320);
        }
    }

    private async Task DismissLaunchOverlayAsync()
    {
        if (LaunchOverlay == null || LaunchOverlay.Visibility != Visibility.Visible) return;

        var elapsed = DateTime.Now - _launchOverlayShownAt;
        var minDuration = TimeSpan.FromMilliseconds(900);
        if (elapsed < minDuration)
        {
            await Task.Delay(minDuration - elapsed);
        }

        var tcs = new TaskCompletionSource<bool>();
        var fade = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, __) =>
        {
            LaunchOverlay.Visibility = Visibility.Collapsed;
            LaunchOverlay.IsHitTestVisible = false;
            tcs.TrySetResult(true);
        };

        var scaleX = new DoubleAnimation
        {
            To = 0.98,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var scaleY = new DoubleAnimation
        {
            To = 0.98,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        LaunchOverlay.BeginAnimation(UIElement.OpacityProperty, fade);
        LaunchCardScale?.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        LaunchCardScale?.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);

        await tcs.Task;
    }

    private async Task AnimateNavRailAsync(bool expand, int durationMs)
    {
        if (NavRail == null) return;
        if (_isNavAnimating) return;

        _isNavAnimating = true;
        var tcs = new TaskCompletionSource<bool>();

        if (expand)
        {
            if (NavBrandIconHost != null)
            {
                NavBrandIconHost.Visibility = Visibility.Visible;
                NavBrandIconHost.Opacity = 1;
            }
            foreach (var label in GetNavLabels())
            {
                label.Visibility = Visibility.Visible;
            }
        }
        else
        {
            if (NavBrandIconHost != null)
            {
                NavBrandIconHost.Visibility = Visibility.Visible;
                NavBrandIconHost.Opacity = 0;
            }
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var widthDuration = TimeSpan.FromMilliseconds(durationMs);
        var labelFadeDuration = TimeSpan.FromMilliseconds(expand ? 220 : 180);
        var labelSlideDuration = TimeSpan.FromMilliseconds(expand ? 260 : 220);
        var labelBeginTime = expand ? TimeSpan.FromMilliseconds(80) : TimeSpan.Zero;
        var brandFadeDuration = TimeSpan.FromMilliseconds(200);
        var brandBeginTime = expand ? TimeSpan.Zero : TimeSpan.FromMilliseconds(60);
        var sb = new Storyboard();

        var widthAnim = new DoubleAnimation
        {
            To = expand ? NavExpandedWidth : NavCollapsedWidth,
            Duration = widthDuration,
            EasingFunction = ease
        };
        Storyboard.SetTarget(widthAnim, NavRail);
        Storyboard.SetTargetProperty(widthAnim, new PropertyPath(FrameworkElement.WidthProperty));
        sb.Children.Add(widthAnim);

        foreach (var label in GetNavLabels())
        {
            var opacityAnim = new DoubleAnimation
            {
                To = expand ? 1 : 0,
                BeginTime = labelBeginTime,
                Duration = labelFadeDuration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(opacityAnim, label);
            Storyboard.SetTargetProperty(opacityAnim, new PropertyPath(UIElement.OpacityProperty));
            sb.Children.Add(opacityAnim);

            var offsetAnim = new DoubleAnimation
            {
                To = expand ? 0 : 8,
                BeginTime = labelBeginTime,
                Duration = labelSlideDuration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(offsetAnim, label);
            Storyboard.SetTargetProperty(offsetAnim, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.X)"));
            sb.Children.Add(offsetAnim);
        }

        if (NavBrandIconHost != null)
        {
            var brandIconOpacityAnim = new DoubleAnimation
            {
                To = expand ? 0 : 1,
                BeginTime = brandBeginTime,
                Duration = brandFadeDuration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(brandIconOpacityAnim, NavBrandIconHost);
            Storyboard.SetTargetProperty(brandIconOpacityAnim, new PropertyPath(UIElement.OpacityProperty));
            sb.Children.Add(brandIconOpacityAnim);
        }

        sb.Completed += (_, __) =>
        {
            if (!expand)
            {
                foreach (var label in GetNavLabels())
                {
                    label.Visibility = Visibility.Collapsed;
                }
                if (NavBrandIconHost != null) NavBrandIconHost.Visibility = Visibility.Visible;
            }
            else
            {
                if (NavBrandIconHost != null) NavBrandIconHost.Visibility = Visibility.Collapsed;
            }
            _isNavAnimating = false;
            tcs.TrySetResult(true);
        };

        sb.Begin();
        await tcs.Task;
    }

    private void SetNavRailVisualState(bool expanded)
    {
        if (NavRail == null) return;
        NavRail.Width = expanded ? NavExpandedWidth : NavCollapsedWidth;
        if (NavBrandIconHost != null) NavBrandIconHost.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        foreach (var label in GetNavLabels())
        {
            label.Opacity = expanded ? 1 : 0;
            label.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            if (label.RenderTransform is TranslateTransform tf)
            {
                tf.X = expanded ? 0 : 8;
            }
        }
    }

    private IEnumerable<System.Windows.Controls.TextBlock> GetNavLabels()
    {
        return new[]
        {
            NavDeviceText,
            NavInstallText,
            NavAppsText,
            NavLogText,
            NavAboutText,
            NavBrandTextA,
            NavBrandTextB
        };
    }

    private async void NavRail_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_isNavPinned || _isNavAnimating) return;
        if (_navHoverIntentTimer == null) return;
        _navHoverExpandTarget = true;
        _navHoverIntentTimer.Stop();
        _navHoverIntentTimer.Start();
        await Task.CompletedTask;
    }

    private async void NavRail_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_isNavPinned || _isNavAnimating) return;
        if (_navHoverIntentTimer == null) return;
        _navHoverExpandTarget = false;
        _navHoverIntentTimer.Stop();
        _navHoverIntentTimer.Start();
        await Task.CompletedTask;
    }

    private async void NavPinToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isNavAnimating) return;

        if (_isNavPinned)
        {
            _isNavPinned = false;
            UpdateNavPinToggleVisual();
            _navHoverIntentTimer?.Stop();
            await AnimateNavRailAsync(expand: false, durationMs: 320);
            return;
        }

        _isNavPinned = true;
        UpdateNavPinToggleVisual();
        _navHoverIntentTimer?.Stop();
        await AnimateNavRailAsync(expand: true, durationMs: 380);
    }


    private void NavThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        ApplyTheme(!_isDarkMode, persist: true);
        ShowNotice(_isDarkMode ? "已切换到黑夜模式。" : "已切换到浅色模式。", "info");
    }

    private void UpdateNavPinToggleVisual()
    {
        if (NavPinToggleButton == null || NavPinToggleIcon == null) return;

        var text = _isNavPinned ? "收起侧栏" : "展开侧栏";
        NavPinToggleButton.ToolTip = text;
        NavPinToggleIcon.Text = _isNavPinned ? "\uE76B" : "\uE76C";
    }

    private void WirePageEvents()
    {
        RefreshButton.Click += Refresh_Click;
        InstallHeaderRefreshButton.Click += Refresh_Click;
        RestartButton.Click += RestartAdb_Click;
        GuideButton.Click += Guide_Click;
        OpenLogButton.Click += OpenLog_Click;
        CheckUpdateButton.Click += CheckUpdate_Click;
        DeviceInfoScrollLeftButton.Click += DeviceInfoScrollLeft_Click;
        DeviceInfoScrollRightButton.Click += DeviceInfoScrollRight_Click;
        ExpCaptureScreenButton.Click += ExpCaptureScreen_Click;
        ExpStartRecordButton.Click += ExpStartRecord_Click;
        ExpStopRecordButton.Click += ExpStopRecord_Click;
        ExpRunAutomationButton.Click += ExpRunAutomation_Click;
        ExpFillAutomationTemplateButton.Click += ExpFillAutomationTemplate_Click;

        InstallCardBorder.PreviewDragOver += InstallArea_PreviewDragOver;
        InstallCardBorder.DragLeave += InstallArea_DragLeave;
        InstallCardBorder.Drop += InstallArea_Drop;
        ApkPathBox.TextChanged += ApkPathBox_TextChanged;
        ApkPathBox.PreviewDragOver += InstallArea_PreviewDragOver;
        ApkPathBox.DragLeave += InstallArea_DragLeave;
        ApkPathBox.Drop += InstallArea_Drop;
        PickApkButton.Click += PickApk_Click;
        InstallApkButton.Click += InstallApk_Click;
        AddQueueButton.Click += AddQueueButton_Click;
        StartQueueButton.Click += StartQueueButton_Click;
        ClearQueueButton.Click += ClearQueueButton_Click;
        RetryFailedButton.Click += RetryFailedButton_Click;
        RemoveSelectedButton.Click += RemoveSelectedButton_Click;
        QueueListBox.SelectionChanged += QueueListBox_SelectionChanged;
        SendTextButton.Click += SendText_Click;

        AppRefreshButton.Click += AppRefresh_Click;
        AppIncludeSystemCheckBox.Checked += AppIncludeSystemChanged_Click;
        AppIncludeSystemCheckBox.Unchecked += AppIncludeSystemChanged_Click;
        AppSearchBox.TextChanged += AppSearchBox_TextChanged;
        AppListBox.SelectionChanged += AppListBox_SelectionChanged;
        AppLaunchButton.Click += AppLaunch_Click;
        AppUninstallButton.Click += AppUninstall_Click;
        AppCopyPackageButton.Click += AppCopyPackage_Click;

        ToggleLogButton.Click += ToggleLogCollapse_Click;
        ClearLogButton.Click += ClearLog_Click;
        CopyLogButton.Click += CopyLog_Click;
        ClearHistoryButton.Click += ClearHistory_Click;
        CopyHistoryButton.Click += CopyHistory_Click;
        AuthorButton.Click += OpenAuthor_Click;
    }

    private void SwitchPage(AppPage page)
    {
        _activePage = page;
        DevicePageHost.Visibility = page == AppPage.Device ? Visibility.Visible : Visibility.Collapsed;
        InstallPageHost.Visibility = page == AppPage.Install ? Visibility.Visible : Visibility.Collapsed;
        AppManagePageHost.Visibility = page == AppPage.Apps ? Visibility.Visible : Visibility.Collapsed;
        LogPageHost.Visibility = page == AppPage.Log ? Visibility.Visible : Visibility.Collapsed;
        AboutPageHost.Visibility = page == AppPage.About ? Visibility.Visible : Visibility.Collapsed;
        ApplyNavSelectionVisuals(page);

        if (page == AppPage.Apps && _isDeviceReady && _managedApps.Count == 0 && !_isBusy)
        {
            _ = RefreshManagedAppsAsync(userInitiated: false);
        }
    }

    private void ApplyNavSelectionVisuals(AppPage page)
    {
        var idleText = ResolveBrush("NavTextIdleBrush", "#8A8A90");
        var activeText = ResolveBrush("NavTextActiveBrush", "#1F1F23");
        var idleIcon = ResolveBrush("NavIconIdleBrush", "#C5C5C9");
        var activeIcon = ResolveBrush("NavIconActiveBrush", "#1C1C1F");
        var activeBg = ResolveBrush("NavItemActiveBrush", "#D8D8DC");

        SetNavItemVisual(page == AppPage.Device, NavDeviceButton, NavDeviceIndicator, NavDeviceIcon, NavDeviceText, idleIcon, activeIcon, idleText, activeText, activeBg);
        SetNavItemVisual(page == AppPage.Install, NavInstallButton, NavInstallIndicator, NavInstallIcon, NavInstallText, idleIcon, activeIcon, idleText, activeText, activeBg);
        SetNavItemVisual(page == AppPage.Apps, NavAppsButton, NavAppsIndicator, NavAppsIcon, NavAppsText, idleIcon, activeIcon, idleText, activeText, activeBg);
        SetNavItemVisual(page == AppPage.Log, NavLogButton, NavLogIndicator, NavLogIcon, NavLogText, idleIcon, activeIcon, idleText, activeText, activeBg);
        SetNavItemVisual(page == AppPage.About, NavAboutButton, NavAboutIndicator, NavAboutIcon, NavAboutText, idleIcon, activeIcon, idleText, activeText, activeBg);
    }

    private static void SetNavItemVisual(
        bool isActive,
        System.Windows.Controls.Button button,
        System.Windows.Controls.Border indicator,
        System.Windows.Controls.TextBlock icon,
        System.Windows.Controls.TextBlock label,
        Brush idleIcon,
        Brush activeIcon,
        Brush idleText,
        Brush activeText,
        Brush activeBackground)
    {
        indicator.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        button.Background = isActive ? activeBackground : Brushes.Transparent;
        icon.Foreground = isActive ? activeIcon : idleIcon;
        label.Foreground = isActive ? activeText : idleText;
    }

    private void NavDevice_Click(object sender, RoutedEventArgs e) => SwitchPage(AppPage.Device);
    private void NavInstall_Click(object sender, RoutedEventArgs e) => SwitchPage(AppPage.Install);
    private void NavApps_Click(object sender, RoutedEventArgs e) => SwitchPage(AppPage.Apps);
    private void NavLog_Click(object sender, RoutedEventArgs e) => SwitchPage(AppPage.Log);
    private void NavAbout_Click(object sender, RoutedEventArgs e) => SwitchPage(AppPage.About);

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout();

    private void Guide_Click(object sender, RoutedEventArgs e) => ToggleGuideDrawer();

    private void GuideDrawerClose_Click(object sender, RoutedEventArgs e) => SetGuideDrawerVisible(false);

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(LogBox.Text ?? ""); } catch { }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e) => _operationHistory.Clear();

    private void CopyHistory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var lines = _operationHistory.Select(x => $"{x.TimeText} {x.Category} {x.Action} {x.Target} {x.Result}");
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
        }
        catch { }
    }

    private void AddOperationHistory(string category, string action, string target, string result)
    {
        var entry = new OperationHistoryItem
        {
            TimeText = DateTime.Now.ToString("HH:mm:ss"),
            Category = category,
            Action = action,
            Target = string.IsNullOrWhiteSpace(target) ? "-" : target,
            Result = string.IsNullOrWhiteSpace(result) ? "-" : result
        };
        _operationHistory.Insert(0, entry);
        while (_operationHistory.Count > 300)
        {
            _operationHistory.RemoveAt(_operationHistory.Count - 1);
        }
    }

    private void SetAppRefreshProgress(bool visible, int current, int total, string message)
    {
        if (AppRefreshProgressPanel == null || AppRefreshProgressText == null || AppRefreshProgressValueText == null || AppRefreshProgressBar == null) return;

        if (!visible)
        {
            AppRefreshProgressPanel.Visibility = Visibility.Collapsed;
            AppRefreshProgressBar.Value = 0;
            AppRefreshProgressText.Text = "";
            AppRefreshProgressValueText.Text = "";
            return;
        }

        AppRefreshProgressPanel.Visibility = Visibility.Visible;
        AppRefreshProgressText.Text = string.IsNullOrWhiteSpace(message) ? "正在处理中..." : message;

        if (total <= 0)
        {
            AppRefreshProgressBar.IsIndeterminate = true;
            AppRefreshProgressValueText.Text = "";
            return;
        }

        AppRefreshProgressBar.IsIndeterminate = false;
        AppRefreshProgressBar.Minimum = 0;
        AppRefreshProgressBar.Maximum = total;
        AppRefreshProgressBar.Value = Math.Max(0, Math.Min(current, total));
        var percent = (int)Math.Round((AppRefreshProgressBar.Value / total) * 100, MidpointRounding.AwayFromZero);
        AppRefreshProgressValueText.Text = $"{current}/{total} ({percent}%)";
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
        AddOperationHistory("安装", "安装队列", $"待处理 {_installQueue.Count(x => x.Status is QueuePending or QueueFailed)} 项", "开始");
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
            AddOperationHistory("安装", "安装队列", "全部任务", "完成");
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
            AddOperationHistory("安装", "安装APK", item.FileName, "成功");
        }
        else
        {
            _installFailCount++;
            var reason = ExtractInstallFailureReason(combined);
            var advice = ExtractInstallFailureAdvice(combined);
            item.MarkCompleted(false, DateTime.Now - startedAt, reason, advice);
            AppendLog($"[Install] 失败原因: {reason}");
            AppendLog($"[Install] 建议处理: {advice}");
            AddOperationHistory("安装", "安装APK", item.FileName, $"失败（{reason}）");
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
        UpdateExtendedQueueActions(canEdit, hasAny, hasPending, hasFailed);
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

    private void InitLogFile()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuestADBTool", "logs"),
            Path.Combine(Path.GetTempPath(), "QuestADBTool", "logs")
        };

        foreach (var dir in candidates)
        {
            try
            {
                Directory.CreateDirectory(dir);
                _logFilePath = Path.Combine(dir, $"log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

                SafeAppendFile(
                    $"=== QuestADBTool Log ==={Environment.NewLine}" +
                    $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                    $"OS: {Environment.OSVersion}{Environment.NewLine}" +
                    $"AppBase: {AppContext.BaseDirectory}{Environment.NewLine}" +
                    $"========================{Environment.NewLine}{Environment.NewLine}");

                AppendLog($"[Log] Log file: {_logFilePath}");
                return;
            }
            catch
            {
                _logFilePath = "";
            }
        }

        _logFilePath = "";
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunAdb(string args, bool logCommand = true, bool logOutput = true)
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

        if (logOutput && !string.IsNullOrWhiteSpace(stdOut)) AppendLog(stdOut.TrimEnd());
        if (logOutput && !string.IsNullOrWhiteSpace(stdErr)) AppendLog(stdErr.TrimEnd());
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
            try
            {
                File.AppendAllText(_logFilePath, content, Encoding.UTF8);
            }
            catch
            {
                // Keep app available even when file logging is unavailable.
            }
        }
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
        if (InstallHeaderRefreshButton != null) InstallHeaderRefreshButton.IsEnabled = !isBusy;
        if (RestartButton != null) RestartButton.IsEnabled = !isBusy;
        if (OpenLogButton != null) OpenLogButton.IsEnabled = !isBusy;
        if (GuideButton != null) GuideButton.IsEnabled = !isBusy;
        if (ExpReadDisplayButton != null) ExpReadDisplayButton.IsEnabled = !isBusy;
        if (ExpApplyResolutionButton != null) ExpApplyResolutionButton.IsEnabled = !isBusy;
        if (ExpResetResolutionButton != null) ExpResetResolutionButton.IsEnabled = !isBusy;
        if (ExpApplyRefreshButton != null) ExpApplyRefreshButton.IsEnabled = !isBusy;
        if (ExpRefresh90Button != null) ExpRefresh90Button.IsEnabled = !isBusy;
        if (ExpRefresh120Button != null) ExpRefresh120Button.IsEnabled = !isBusy;
        if (ExpRunAutomationButton != null) ExpRunAutomationButton.IsEnabled = !isBusy;
        if (ExpFillAutomationTemplateButton != null) ExpFillAutomationTemplateButton.IsEnabled = !isBusy;
        if (ExpAutomationScriptBox != null) ExpAutomationScriptBox.IsEnabled = !isBusy;
        if (ClearLogButton != null) ClearLogButton.IsEnabled = !isBusy;
        if (CopyLogButton != null) CopyLogButton.IsEnabled = !isBusy;
        if (ClearHistoryButton != null) ClearHistoryButton.IsEnabled = !isBusy;
        if (CopyHistoryButton != null) CopyHistoryButton.IsEnabled = !isBusy;
        if (AuthorButton != null) AuthorButton.IsEnabled = !isBusy;
        if (ToggleLogButton != null) ToggleLogButton.IsEnabled = !isBusy;
        if (AppRefreshButton != null) AppRefreshButton.IsEnabled = !isBusy;
        if (AppIncludeSystemCheckBox != null) AppIncludeSystemCheckBox.IsEnabled = !isBusy;
        if (AppSearchBox != null) AppSearchBox.IsEnabled = !isBusy;
        if (AppListBox != null) AppListBox.IsEnabled = !isBusy;

        if (BusyText != null)
        {
            BusyText.Text = message ?? "";
            BusyText.Visibility = (isBusy && !string.IsNullOrWhiteSpace(message)) ? Visibility.Visible : Visibility.Collapsed;
        }

        UpdateQueueActionButtons();
        UpdateAppManageActionButtons();
        UpdateScreenRecordStateUi();
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


    private void InitSettingsFile()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuestADBTool");
        Directory.CreateDirectory(dir);
        _settingsFilePath = Path.Combine(dir, "settings.json");
    }

    private bool LoadThemeSetting()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_settingsFilePath) || !File.Exists(_settingsFilePath)) return false;
            var json = File.ReadAllText(_settingsFilePath, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            return settings?.DarkMode == true;
        }
        catch
        {
            return false;
        }
    }

    private void SaveThemeSetting()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_settingsFilePath)) return;
            var settings = new AppSettings { DarkMode = _isDarkMode };
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFilePath, json, Encoding.UTF8);
        }
        catch { }
    }

    private void ApplyTheme(bool darkMode, bool persist)
    {
        _isDarkMode = darkMode;

        if (darkMode)
        {
            SetSolidBrushColor(Application.Current.Resources, "Bg", "#1B1D23");
            SetSolidBrushColor(Application.Current.Resources, "Card", "#1F2229");
            SetSolidBrushColor(Application.Current.Resources, "SurfaceSubtle", "#252931");
            SetSolidBrushColor(Application.Current.Resources, "SurfaceOverlay", "#991A1D23");
            SetSolidBrushColor(Application.Current.Resources, "Text", "#E4E6EB");
            SetSolidBrushColor(Application.Current.Resources, "TextDim", "#9AA1AE");
            SetSolidBrushColor(Application.Current.Resources, "Border", "#323844");
            SetSolidBrushColor(Application.Current.Resources, "BorderStrong", "#424A58");
            SetSolidBrushColor(Application.Current.Resources, "Primary", "#1592FF");
            SetSolidBrushColor(Application.Current.Resources, "PrimaryHover", "#1084E8");
            SetSolidBrushColor(Application.Current.Resources, "PrimaryPressed", "#0D75CF");
            SetSolidBrushColor(Application.Current.Resources, "InfoBg", "#1B273B");
            SetSolidBrushColor(Application.Current.Resources, "InfoBorder", "#2F4C76");
            SetSolidBrushColor(Application.Current.Resources, "InfoText", "#9AC5FF");
            SetSolidBrushColor(Application.Current.Resources, "WarnBg", "#33280F");
            SetSolidBrushColor(Application.Current.Resources, "WarnBorder", "#6B4F1E");
            SetSolidBrushColor(Application.Current.Resources, "WarnText", "#FCD34D");
            SetSolidBrushColor(Application.Current.Resources, "ErrorBg", "#331D22");
            SetSolidBrushColor(Application.Current.Resources, "ErrorBorder", "#7A2E39");
            SetSolidBrushColor(Application.Current.Resources, "ErrorText", "#FCA5A5");
            SetSolidBrushColor(Application.Current.Resources, "SuccessBg", "#173127");
            SetSolidBrushColor(Application.Current.Resources, "SuccessBorder", "#2D5F4A");
            SetSolidBrushColor(Application.Current.Resources, "SuccessText", "#86EFAC");

            SetSolidBrushColor(Resources, "NavRailBgBrush", "#191C22");
            SetSolidBrushColor(Resources, "NavRailBorderBrush", "#2B313C");
            SetSolidBrushColor(Resources, "NavItemHoverBrush", "#242A34");
            SetSolidBrushColor(Resources, "NavItemActiveBrush", "#2D3440");
            SetSolidBrushColor(Resources, "NavIconIdleBrush", "#8892A1");
            SetSolidBrushColor(Resources, "NavIconActiveBrush", "#E4E6EB");
            SetSolidBrushColor(Resources, "NavTextIdleBrush", "#96A0AF");
            SetSolidBrushColor(Resources, "NavTextActiveBrush", "#F3F4F6");

            if (TitleBarBorder != null) TitleBarBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4A4D54"));
        }
        else
        {
            SetSolidBrushColor(Application.Current.Resources, "Bg", "#F3F4F6");
            SetSolidBrushColor(Application.Current.Resources, "Card", "#FFFFFF");
            SetSolidBrushColor(Application.Current.Resources, "SurfaceSubtle", "#F8FAFC");
            SetSolidBrushColor(Application.Current.Resources, "SurfaceOverlay", "#66FFFFFF");
            SetSolidBrushColor(Application.Current.Resources, "Text", "#111827");
            SetSolidBrushColor(Application.Current.Resources, "TextDim", "#6B7280");
            SetSolidBrushColor(Application.Current.Resources, "Border", "#E4E6EB");
            SetSolidBrushColor(Application.Current.Resources, "BorderStrong", "#CBD5E1");
            SetSolidBrushColor(Application.Current.Resources, "Primary", "#C2FF89");
            SetSolidBrushColor(Application.Current.Resources, "PrimaryHover", "#B2F77A");
            SetSolidBrushColor(Application.Current.Resources, "PrimaryPressed", "#9EEA60");
            SetSolidBrushColor(Application.Current.Resources, "InfoBg", "#EFF6FF");
            SetSolidBrushColor(Application.Current.Resources, "InfoBorder", "#BFDBFE");
            SetSolidBrushColor(Application.Current.Resources, "InfoText", "#1D4ED8");
            SetSolidBrushColor(Application.Current.Resources, "WarnBg", "#FFFBEB");
            SetSolidBrushColor(Application.Current.Resources, "WarnBorder", "#FDE68A");
            SetSolidBrushColor(Application.Current.Resources, "WarnText", "#B45309");
            SetSolidBrushColor(Application.Current.Resources, "ErrorBg", "#FEF2F2");
            SetSolidBrushColor(Application.Current.Resources, "ErrorBorder", "#FECACA");
            SetSolidBrushColor(Application.Current.Resources, "ErrorText", "#B91C1C");
            SetSolidBrushColor(Application.Current.Resources, "SuccessBg", "#ECFDF5");
            SetSolidBrushColor(Application.Current.Resources, "SuccessBorder", "#BBF7D0");
            SetSolidBrushColor(Application.Current.Resources, "SuccessText", "#166534");

            SetSolidBrushColor(Resources, "NavRailBgBrush", "#ECECED");
            SetSolidBrushColor(Resources, "NavRailBorderBrush", "#DFDFE2");
            SetSolidBrushColor(Resources, "NavItemHoverBrush", "#E1E1E4");
            SetSolidBrushColor(Resources, "NavItemActiveBrush", "#D8D8DC");
            SetSolidBrushColor(Resources, "NavIconIdleBrush", "#C5C5C9");
            SetSolidBrushColor(Resources, "NavIconActiveBrush", "#1C1C1F");
            SetSolidBrushColor(Resources, "NavTextIdleBrush", "#8A8A90");
            SetSolidBrushColor(Resources, "NavTextActiveBrush", "#1F1F23");

            if (TitleBarBorder != null) TitleBarBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F3F4F6"));
        }

        UpdateThemeToggleVisual();
        ApplyNavSelectionVisuals(_activePage);

        if (persist) SaveThemeSetting();
    }

    private void UpdateThemeToggleVisual()
    {
        if (NavThemeToggleButton == null || NavThemeToggleIcon == null) return;
        NavThemeToggleIcon.Text = _isDarkMode ? "\uE706" : "\uE708";
        NavThemeToggleButton.ToolTip = _isDarkMode ? "切换为浅色模式" : "开启黑夜模式";
        NavThemeToggleIcon.Foreground = ResolveBrush("TextDim", _isDarkMode ? "#9AA1AE" : "#6B7280");
        if (NavPinToggleIcon != null)
        {
            NavPinToggleIcon.Foreground = ResolveBrush("TextDim", _isDarkMode ? "#9AA1AE" : "#6B7280");
        }
    }

    private static void SetSolidBrushColor(ResourceDictionary resourceDictionary, string key, string colorHex)
    {
        var color = (Color)ColorConverter.ConvertFromString(colorHex);
        if (resourceDictionary[key] is SolidColorBrush brush)
        {
            if (!brush.IsFrozen)
            {
                brush.Color = color;
                return;
            }

            resourceDictionary[key] = new SolidColorBrush(color);
            return;
        }

        resourceDictionary[key] = new SolidColorBrush(color);
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
                    ClearQueueButton, RetryFailedButton, RemoveSelectedButton, AuthorButton, ClearLogButton, CopyLogButton, ClearHistoryButton, CopyHistoryButton,
                    AppRefreshButton, AppLaunchButton, AppUninstallButton, AppCopyPackageButton,
                    ExpCaptureScreenButton, ExpStartRecordButton, ExpStopRecordButton,
                    ExpRunAutomationButton, ExpFillAutomationTemplateButton);
                SetControlHeight(32, ApkPathBox, InputTextBox, AppSearchBox);
                SetInstallColumns(1.0, 1.0);
                QueueListBox.Height = 96;
                LogBox.MaxHeight = 140;
                GuideDrawer.Width = 300;
                break;

            case ResponsiveTier.Medium:
                RootLayoutGrid.Margin = new Thickness(10);
                SetControlHeight(34, RefreshButton, RestartButton, GuideButton, OpenLogButton, CheckUpdateButton,
                    PickApkButton, InstallApkButton, SendTextButton, AddQueueButton, StartQueueButton,
                    ClearQueueButton, RetryFailedButton, RemoveSelectedButton, AuthorButton, ClearLogButton, CopyLogButton, ClearHistoryButton, CopyHistoryButton,
                    AppRefreshButton, AppLaunchButton, AppUninstallButton, AppCopyPackageButton,
                    ExpCaptureScreenButton, ExpStartRecordButton, ExpStopRecordButton,
                    ExpRunAutomationButton, ExpFillAutomationTemplateButton);
                SetControlHeight(34, ApkPathBox, InputTextBox, AppSearchBox);
                SetInstallColumns(1.15, 1.0);
                QueueListBox.Height = 108;
                LogBox.MaxHeight = 160;
                GuideDrawer.Width = 330;
                break;

            default:
                RootLayoutGrid.Margin = new Thickness(14);
                SetControlHeight(36, RefreshButton, RestartButton, GuideButton, OpenLogButton, CheckUpdateButton,
                    PickApkButton, InstallApkButton, SendTextButton, AddQueueButton, StartQueueButton,
                    ClearQueueButton, RetryFailedButton, RemoveSelectedButton, AuthorButton, ClearLogButton, CopyLogButton, ClearHistoryButton, CopyHistoryButton,
                    AppRefreshButton, AppLaunchButton, AppUninstallButton, AppCopyPackageButton,
                    ExpCaptureScreenButton, ExpStartRecordButton, ExpStopRecordButton,
                    ExpRunAutomationButton, ExpFillAutomationTemplateButton);
                SetControlHeight(36, ApkPathBox, InputTextBox, AppSearchBox);
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
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        StatusDot.Fill = brush;
        if (InstallDeviceStateDot != null)
        {
            InstallDeviceStateDot.Fill = brush;
        }
        if (InstallDeviceStateText != null)
        {
            InstallDeviceStateText.Text = state switch
            {
                "ok" => "设备已连接",
                "warn" => "等待授权",
                "bad" => "未连接",
                _ => "连接未知"
            };
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true });
        e.Handled = true;
    }
}











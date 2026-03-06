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
    private static readonly HttpClient UpdateHttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private const string UpdateManifestUrl = "https://superpixel-1302573006.cos.ap-nanjing.myqcloud.com/update.json";
    private const double NavCollapsedWidth = 56;
    private const double NavExpandedWidth = 240;
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
        ShowFirstRunTip();
        ApplyResponsiveLayout();

        Loaded += async (_, __) =>
        {
            await PlayNavRailStartupAnimationAsync();
            await RefreshStatus();
            await CheckForUpdatesAsync(userInitiated: false);
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

    private async void AppRefresh_Click(object sender, RoutedEventArgs e) => await RefreshManagedAppsAsync(userInitiated: true);

    private async void AppIncludeSystemChanged_Click(object sender, RoutedEventArgs e) => await RefreshManagedAppsAsync(userInitiated: true);

    private void AppSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyManagedAppFilter();

    private async void AppListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateAppManageActionButtons();
        await RefreshSelectedAppDetailsAsync();
    }

    private async void AppLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedManagedApp(out var app))
        {
            ShowNotice("请先选择一个应用。", "warn");
            return;
        }

        if (app.IsLaunchable == false)
        {
            ShowNotice("该应用没有可启动入口（可能是服务组件）。", "warn");
            return;
        }

        SetBusy(true, "正在启动应用...");
        try
        {
            if (!EnsureAdbExists()) return;
            var state = await GetDeviceState();
            if (!GuardDeviceState(state)) return;

            var result = await RunAdb($"shell monkey -p {app.PackageName} -c android.intent.category.LAUNCHER 1");
            var combined = $"{result.StdOut}\n{result.StdErr}";
            if (result.ExitCode == 0 && !combined.Contains("Exception", StringComparison.OrdinalIgnoreCase))
            {
                AddOperationHistory("应用管理", "启动应用", app.PackageName, "成功");
                ShowNotice($"已尝试启动：{app.PackageName}", "info");
                return;
            }

            AddOperationHistory("应用管理", "启动应用", app.PackageName, "失败");
            ShowNotice("启动失败，请确认该应用存在可启动 Activity。", "warn");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void AppUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedManagedApp(out var app))
        {
            ShowNotice("请先选择一个应用。", "warn");
            return;
        }

        var confirm = MessageBox.Show(
            $"确认卸载以下应用？\n\n{app.PackageName}",
            "确认卸载",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true, "正在卸载应用...");
        try
        {
            if (!EnsureAdbExists()) return;
            var state = await GetDeviceState();
            if (!GuardDeviceState(state)) return;

            var result = await RunAdb($"uninstall {app.PackageName}");
            var combined = $"{result.StdOut}\n{result.StdErr}";
            var success = result.ExitCode == 0 && combined.Contains("Success", StringComparison.OrdinalIgnoreCase);

            if (!success && app.IsSystemApp)
            {
                var fallback = await RunAdb($"shell pm uninstall --user 0 {app.PackageName}");
                combined = $"{fallback.StdOut}\n{fallback.StdErr}";
                success = fallback.ExitCode == 0 && combined.Contains("Success", StringComparison.OrdinalIgnoreCase);
            }

            if (success)
            {
                _managedAppsAll = _managedAppsAll
                    .Where(x => !x.PackageName.Equals(app.PackageName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                ApplyManagedAppFilter();
                AddOperationHistory("应用管理", "卸载应用", app.PackageName, "成功");
                ShowNotice($"已卸载：{app.PackageName}", "info");
                return;
            }

            AddOperationHistory("应用管理", "卸载应用", app.PackageName, "失败");
            ShowNotice("卸载失败，请查看日志。", "warn");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void AppCopyPackage_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedManagedApp(out var app))
        {
            ShowNotice("请先选择一个应用。", "warn");
            return;
        }

        try
        {
            Clipboard.SetText(app.PackageName);
            AddOperationHistory("应用管理", "复制包名", app.PackageName, "成功");
            ShowNotice("包名已复制。", "info");
        }
        catch
        {
            AddOperationHistory("应用管理", "复制包名", app.PackageName, "失败");
            ShowNotice("复制包名失败。", "warn");
        }
    }

    private async Task RefreshManagedAppsAsync(bool userInitiated)
    {
        if (!EnsureAdbExists()) return;

        var state = await GetDeviceState();
        if (!GuardDeviceState(state)) return;

        SetBusy(true, "正在读取应用列表...");
        try
        {
            SetAppRefreshProgress(true, 0, 0, "正在读取应用列表...");
            var includeSystem = AppIncludeSystemCheckBox.IsChecked == true;
            var apps = await QueryManagedAppsAsync(includeSystem);
            await PopulateManagedAppsDumpInfoAsync(apps);
            _managedAppsAll = apps;
            ApplyManagedAppFilter();
            AddOperationHistory("应用管理", "刷新列表", includeSystem ? "包含系统应用" : "仅第三方", $"完成 {apps.Count} 项");

            if (userInitiated)
            {
                ShowNotice($"已刷新应用列表，共 {apps.Count} 项。", "info");
            }
        }
        finally
        {
            SetAppRefreshProgress(false, 0, 0, "");
            SetBusy(false);
        }
    }

    private async Task<List<ManagedApp>> QueryManagedAppsAsync(bool includeSystem)
    {
        var result = await RunAdb("shell pm list packages -f", logCommand: false);
        var apps = ParsePackageListWithPath(result.StdOut);
        if (apps.Count == 0)
        {
            var fallback = await RunAdb("shell pm list packages", logCommand: false);
            apps = ParsePackageList(fallback.StdOut, isSystemApp: false);
        }

        if (!includeSystem)
        {
            apps = apps.Where(x => !x.IsSystemApp).ToList();
        }

        return apps
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.PackageName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task PopulateManagedAppsDumpInfoAsync(List<ManagedApp> apps)
    {
        SetAppRefreshProgress(true, 0, apps.Count, $"正在读取应用详情 0/{apps.Count}");
        var index = 0;
        foreach (var app in apps)
        {
            var dumpResult = await RunAdb($"shell dumpsys package {app.PackageName}", logCommand: false, logOutput: false);
            if (dumpResult.ExitCode == 0)
            {
                var dumpInfo = ParsePackageDumpInfo(dumpResult.StdOut);
                ApplyPackageDumpInfo(app, dumpInfo);
            }

            index++;
            SetAppRefreshProgress(true, index, apps.Count, $"正在读取应用详情 {index}/{apps.Count}");
        }
    }

    private static List<ManagedApp> ParsePackageList(string output, bool isSystemApp)
    {
        return ParsePackageNames(output)
            .Select(x => new ManagedApp
            {
                PackageName = x,
                DisplayName = BuildDisplayNameFromPackage(x),
                IsSystemApp = isSystemApp,
                IsOfficialApp = IsOfficialPackage(x)
            })
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<ManagedApp> ParsePackageListWithPath(string output)
    {
        var apps = new List<ManagedApp>();
        var lines = (output ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var raw = line.Trim();
            if (!raw.StartsWith("package:", StringComparison.OrdinalIgnoreCase)) continue;

            var content = raw["package:".Length..];
            var splitIndex = content.LastIndexOf('=');
            if (splitIndex <= 0 || splitIndex >= content.Length - 1) continue;

            var codePath = content[..splitIndex].Trim();
            var packageName = content[(splitIndex + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(packageName)) continue;

            apps.Add(new ManagedApp
            {
                PackageName = packageName,
                DisplayName = BuildDisplayNameFromPackage(packageName),
                IsSystemApp = IsSystemCodePath(codePath),
                IsOfficialApp = IsOfficialPackage(packageName),
                CodePath = codePath
            });
        }

        return apps;
    }

    private static bool IsSystemCodePath(string codePath)
    {
        if (string.IsNullOrWhiteSpace(codePath)) return false;
        var p = codePath.Trim().ToLowerInvariant();
        return p.StartsWith("/system/") ||
               p.StartsWith("/product/") ||
               p.StartsWith("/vendor/") ||
               p.StartsWith("/odm/") ||
               p.StartsWith("/system_ext/") ||
               p.StartsWith("/apex/");
    }

    private static bool IsOfficialPackage(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName)) return false;
        return packageName.StartsWith("com.meta.", StringComparison.OrdinalIgnoreCase) ||
               packageName.StartsWith("com.oculus.", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildDisplayNameFromPackage(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName)) return "-";
        var token = packageName.Split('.').LastOrDefault() ?? packageName;
        if (string.IsNullOrWhiteSpace(token)) return packageName;

        // Convert common package token styles to readable title.
        var words = Regex.Matches(token, @"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|\d+")
            .Cast<Match>()
            .Select(m => m.Value)
            .ToList();
        if (words.Count == 0) words = token.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count == 0) return packageName;

        return string.Join(" ", words.Select(CapitalizeToken));
    }

    private static string CapitalizeToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return token;
        if (token.All(char.IsDigit)) return token;
        if (token.Length == 1) return token.ToUpperInvariant();
        return char.ToUpperInvariant(token[0]) + token[1..].ToLowerInvariant();
    }

    private static PackageDumpInfo ParsePackageDumpInfo(string dumpOutput)
    {
        var output = dumpOutput ?? "";

        var info = new PackageDumpInfo
        {
            DisplayName = MatchPackageDumpValue(output, @"application-label(?:-[^:]+)?:\s*'?(?<v>[^'\r\n]+)'?"),
            VersionName = MatchPackageDumpValue(output, @"versionName=(?<v>[^\r\n]+)"),
            VersionCode = MatchPackageDumpValue(output, @"versionCode=(?<v>\d+)"),
            CodePath = MatchPackageDumpValue(output, @"codePath=(?<v>[^\r\n]+)"),
            FirstInstallTime = MatchPackageDumpValue(output, @"firstInstallTime=(?<v>[^\r\n]+)"),
            LastUpdateTime = MatchPackageDumpValue(output, @"lastUpdateTime=(?<v>[^\r\n]+)")
        };

        return info;
    }

    private static string? MatchPackageDumpValue(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var value = match.Groups["v"].Value.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static void ApplyPackageDumpInfo(ManagedApp app, PackageDumpInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.DisplayName)) app.DisplayName = info.DisplayName;
        if (!string.IsNullOrWhiteSpace(info.VersionName)) app.VersionName = info.VersionName;
        if (!string.IsNullOrWhiteSpace(info.VersionCode)) app.VersionCode = info.VersionCode;
        if (!string.IsNullOrWhiteSpace(info.CodePath)) app.CodePath = info.CodePath;
        if (!string.IsNullOrWhiteSpace(info.FirstInstallTime)) app.FirstInstallTime = info.FirstInstallTime;
        if (!string.IsNullOrWhiteSpace(info.LastUpdateTime)) app.LastUpdateTime = info.LastUpdateTime;
    }

    private static HashSet<string> ParsePackageNames(string output)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = (output ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var raw = line.Trim();
            if (!raw.StartsWith("package:", StringComparison.OrdinalIgnoreCase)) continue;
            var packageName = raw["package:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(packageName)) continue;
            names.Add(packageName);
        }
        return names;
    }

    private void ApplyManagedAppFilter()
    {
        var keyword = (AppSearchBox.Text ?? "").Trim();
        var selectedPackage = (AppListBox.SelectedItem as ManagedApp)?.PackageName;

        IEnumerable<ManagedApp> query = _managedAppsAll;
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(x =>
                x.PackageName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                x.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = query
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.PackageName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _managedApps.Clear();
        foreach (var app in filtered) _managedApps.Add(app);

        if (!string.IsNullOrWhiteSpace(selectedPackage))
        {
            AppListBox.SelectedItem = _managedApps.FirstOrDefault(x =>
                x.PackageName.Equals(selectedPackage, StringComparison.OrdinalIgnoreCase));
        }

        UpdateAppManageActionButtons();
    }

    private bool TryGetSelectedManagedApp(out ManagedApp app)
    {
        if (AppListBox.SelectedItem is ManagedApp selected)
        {
            app = selected;
            return true;
        }

        app = null!;
        return false;
    }

    private async Task RefreshSelectedAppDetailsAsync()
    {
        if (!TryGetSelectedManagedApp(out var app)) return;
        if (!_isDeviceReady || _isBusy) return;

        if (app.IsLaunchable == null)
        {
            var resolveResult = await RunAdb($"shell cmd package resolve-activity --brief {app.PackageName}", logCommand: false, logOutput: false);
            var combined = $"{resolveResult.StdOut}\n{resolveResult.StdErr}";
            app.IsLaunchable = resolveResult.ExitCode == 0 &&
                               combined.Contains("/", StringComparison.Ordinal) &&
                               !combined.Contains("No activity", StringComparison.OrdinalIgnoreCase);
        }

        UpdateAppManageActionButtons();
    }

    private void UpdateAppManageActionButtons()
    {
        var hasSelection = TryGetSelectedManagedApp(out var app);
        var canOperate = _isDeviceReady && !_isBusy;

        AppRefreshButton.IsEnabled = !_isBusy;
        AppIncludeSystemCheckBox.IsEnabled = !_isBusy;
        AppSearchBox.IsEnabled = !_isBusy;
        AppListBox.IsEnabled = !_isBusy;
        AppLaunchButton.IsEnabled = canOperate && hasSelection;
        AppUninstallButton.IsEnabled = canOperate && hasSelection;
        AppCopyPackageButton.IsEnabled = hasSelection;
        AppCountText.Text = $"应用数：{_managedApps.Count}";

        if (!hasSelection)
        {
            AppSelectedPackageText.Text = "包名：-";
            return;
        }

        var appType = app.IsOfficialApp ? "官方应用" : (app.IsSystemApp ? "系统应用" : "第三方应用");
        var launchableText = app.IsLaunchable == null ? "检测中/未检测" : (app.IsLaunchable == true ? "可启动" : "无可启动入口");
        AppSelectedPackageText.Text =
            $"名称：{app.DisplayName}\n" +
            $"包名：{app.PackageName}\n" +
            $"类型：{appType}\n" +
            $"版本：{app.VersionName} (code {app.VersionCode})\n" +
            $"安装路径：{app.CodePath}\n" +
            $"首次安装：{app.FirstInstallTime}\n" +
            $"最后更新：{app.LastUpdateTime}\n" +
            $"启动状态：{launchableText}";
    }

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
            AddOperationHistory("输入", "发送文本", text.Length > 24 ? text[..24] + "..." : text, "成功");
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


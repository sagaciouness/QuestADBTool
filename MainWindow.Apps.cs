using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
namespace QuestADBTool;
public partial class MainWindow
{

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
        UpdateExtendedAppActions(canOperate, hasSelection);
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

}
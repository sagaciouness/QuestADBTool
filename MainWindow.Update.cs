using System.Net.Http;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
namespace QuestADBTool;
public partial class MainWindow
{

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(userInitiated: true);

    private void UpdateGithub_Click(object sender, RoutedEventArgs e) => OpenUrlSafe(_latestUpdate?.Downloads?.GithubUrl);

    private void UpdateBaidu_Click(object sender, RoutedEventArgs e) => OpenUrlSafe(_latestUpdate?.Downloads?.BaiduUrl);


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

}
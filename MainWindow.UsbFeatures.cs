using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace QuestADBTool;

public partial class MainWindow
{
    private const string DiagnosticsPlaceholderText = "尚未运行诊断。";

    private sealed class ApkManifestInfo
    {
        public string PackageName { get; set; } = "-";
        public string VersionName { get; set; } = "-";
        public string VersionCode { get; set; } = "-";
        public string MinSdk { get; set; } = "-";
        public string TargetSdk { get; set; } = "-";
        public string AbiSummary { get; set; } = "-";
        public string SignatureSummary { get; set; } = "-";
    }

    private readonly object _activeAdbProcessLock = new();
    private Process? _activeAdbProcess;
    private bool _queuePauseRequested;
    private bool _queueStopRequested;

    private Button RunDiagnosticsButton => DevicePageHost.RunDiagnosticsButtonControl;
    private Button CopyDiagnosticsButton => DevicePageHost.CopyDiagnosticsButtonControl;
    private TextBox DiagnosticsBox => DevicePageHost.DiagnosticsBoxControl;

    private TextBlock ApkInfoSdkText => InstallPageHost.ApkInfoSdkTextControl;
    private TextBlock ApkInfoAbiText => InstallPageHost.ApkInfoAbiTextControl;
    private TextBlock ApkInfoSignatureText => InstallPageHost.ApkInfoSignatureTextControl;
    private Button PauseQueueButton => InstallPageHost.PauseQueueButtonControl;
    private Button StopQueueButton => InstallPageHost.StopQueueButtonControl;
    private Button ExportQueueResultButton => InstallPageHost.ExportQueueResultButtonControl;

    private Button AppForceStopButton => AppManagePageHost.AppForceStopButtonControl;
    private Button AppClearDataButton => AppManagePageHost.AppClearDataButtonControl;
    private Button AppExportApkButton => AppManagePageHost.AppExportApkButtonControl;
    private Button AppOpenSettingsButton => AppManagePageHost.AppOpenSettingsButtonControl;
    private Button AppReadPermissionsButton => AppManagePageHost.AppReadPermissionsButtonControl;

    private void InitializeUsbOnlyExtensions()
    {
        RunDiagnosticsButton.Click += RunDiagnostics_Click;
        CopyDiagnosticsButton.Click += CopyDiagnostics_Click;
        PauseQueueButton.Click += PauseQueueButton_Click;
        StopQueueButton.Click += StopQueueButton_Click;
        ExportQueueResultButton.Click += ExportQueueResultButton_Click;
        AppForceStopButton.Click += AppForceStop_Click;
        AppClearDataButton.Click += AppClearData_Click;
        AppExportApkButton.Click += AppExportApk_Click;
        AppOpenSettingsButton.Click += AppOpenSettings_Click;
        AppReadPermissionsButton.Click += AppReadPermissions_Click;
        DiagnosticsBox.Text = DiagnosticsPlaceholderText;
    }

    private void UpdateExtendedQueueActions(bool canEdit, bool hasAny, bool hasPending, bool hasFailed)
    {
        var hasRunning = _isQueueRunning;
        PauseQueueButton.IsEnabled = hasRunning && !_isBusy;
        StopQueueButton.IsEnabled = hasRunning && !_isBusy;
        ExportQueueResultButton.IsEnabled = !_isBusy && hasAny;
        PauseQueueButton.Content = _queuePauseRequested ? "暂停中..." : "暂停后续";
        StopQueueButton.Content = _queueStopRequested ? "停止中..." : "停止队列";
    }

    private void UpdateExtendedAppActions(bool canOperate, bool hasSelection)
    {
        AppForceStopButton.IsEnabled = canOperate && hasSelection;
        AppClearDataButton.IsEnabled = canOperate && hasSelection;
        AppExportApkButton.IsEnabled = canOperate && hasSelection;
        AppOpenSettingsButton.IsEnabled = canOperate && hasSelection;
        AppReadPermissionsButton.IsEnabled = canOperate && hasSelection;
        RunDiagnosticsButton.IsEnabled = !_isBusy;
        CopyDiagnosticsButton.IsEnabled = !string.IsNullOrWhiteSpace(DiagnosticsBox.Text) && DiagnosticsBox.Text != DiagnosticsPlaceholderText;
    }

    private void AttachActiveAdbProcess(Process process)
    {
        lock (_activeAdbProcessLock)
        {
            _activeAdbProcess = process;
        }
    }

    private void DetachActiveAdbProcess(Process process)
    {
        lock (_activeAdbProcessLock)
        {
            if (ReferenceEquals(_activeAdbProcess, process))
            {
                _activeAdbProcess = null;
            }
        }
    }

    private void AbortActiveAdbProcess()
    {
        lock (_activeAdbProcessLock)
        {
            if (_activeAdbProcess == null) return;
            try
            {
                if (!_activeAdbProcess.HasExited)
                {
                    _activeAdbProcess.Kill();
                }
            }
            catch
            {
            }
        }
    }

    private async void RunDiagnostics_Click(object sender, RoutedEventArgs e) => await RunConnectionDiagnosticsAsync();

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DiagnosticsBox.Text ?? string.Empty);
            ShowNotice("诊断结果已复制。", "info");
        }
        catch
        {
            ShowNotice("复制诊断结果失败。", "warn");
        }
    }

    private async Task RunConnectionDiagnosticsAsync()
    {
        SetBusy(true, "正在运行连接诊断...");
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"诊断时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"ADB 路径：{AdbPath}");
            sb.AppendLine($"ADB 存在：{(File.Exists(AdbPath) ? "是" : "否")}");

            if (!File.Exists(AdbPath))
            {
                sb.AppendLine();
                sb.AppendLine("诊断结果：缺少 adb 组件。");
                DiagnosticsBox.Text = sb.ToString().TrimEnd();
                return;
            }

            var version = await RunAdb("version", logCommand: false, logOutput: false);
            var devices = await RunAdb("devices -l", logCommand: false, logOutput: false);
            var getState = await RunAdb("get-state", logCommand: false, logOutput: false);

            sb.AppendLine();
            sb.AppendLine("[ADB 版本]");
            sb.AppendLine(FirstNonEmpty(version.StdOut, version.StdErr, "-"));
            sb.AppendLine();
            sb.AppendLine("[ADB 设备列表]");
            sb.AppendLine(string.IsNullOrWhiteSpace(devices.StdOut) ? "-" : devices.StdOut.TrimEnd());
            if (!string.IsNullOrWhiteSpace(devices.StdErr))
            {
                sb.AppendLine("[错误输出]");
                sb.AppendLine(devices.StdErr.TrimEnd());
            }
            sb.AppendLine();
            sb.AppendLine($"[ADB 设备状态] {(string.IsNullOrWhiteSpace(getState.StdOut) ? getState.StdErr.Trim() : getState.StdOut.Trim())}");
            sb.AppendLine();
            sb.AppendLine("[建议]");
            foreach (var line in BuildDiagnosticAdvice(devices.StdOut, devices.StdErr, getState.StdOut, getState.StdErr))
            {
                sb.AppendLine($"- {line}");
            }

            DiagnosticsBox.Text = sb.ToString().TrimEnd();
            ShowNotice("连接诊断已完成。", "info");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "-";

    private static IEnumerable<string> BuildDiagnosticAdvice(string devicesOut, string devicesErr, string stateOut, string stateErr)
    {
        var combined = $"{devicesOut}\n{devicesErr}\n{stateOut}\n{stateErr}";
        if (combined.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            yield return "设备已被识别，但尚未授权 USB 调试。";
            yield return "请戴上头显点击“允许 USB 调试”，必要时重新插拔数据线后再试。";
            yield break;
        }

        if (combined.Contains("offline", StringComparison.OrdinalIgnoreCase))
        {
            yield return "设备当前处于 offline，通常与线材、USB 接口或授权状态有关。";
            yield return "建议优先更换主板 USB 2.0 Type-A 接口，再执行“修复连接”。";
            yield break;
        }

        if (combined.Contains("device", StringComparison.OrdinalIgnoreCase))
        {
            yield return "ADB 已识别到可用设备，当前数据链路正常。";
            yield return "如果个别功能失败，请优先查看该功能对应的详细日志。";
            yield break;
        }

        yield return "当前未检测到可用设备，请确认头显已开机、开发者模式已开启、数据线支持数据传输。";
        yield return "如果系统只能给头显充电而不能识别设备，通常是线材不支持数据传输。";
    }

    private void PauseQueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isQueueRunning) return;
        _queuePauseRequested = true;
        UpdateQueueActionButtons();
        ShowNotice("当前任务完成后会暂停后续队列。", "info");
    }

    private void StopQueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isQueueRunning) return;
        _queueStopRequested = true;
        AbortActiveAdbProcess();
        UpdateQueueActionButtons();
        ShowNotice("已请求停止队列，当前命令会尽快结束。", "warn");
    }

    private void ExportQueueResultButton_Click(object sender, RoutedEventArgs e)
    {
        var dir = EnsureMediaOutputDirectory(Environment.SpecialFolder.MyDocuments);
        var file = Path.Combine(dir, $"install_queue_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        var lines = _installQueue.Select(x => $"{x.FileName}\t{x.StatusDisplay}\t尝试次数:{x.AttemptCount}\t耗时:{x.LastElapsedDisplay}\t{x.LastReason}\t{x.LastAdvice}");
        File.WriteAllLines(file, lines, Encoding.UTF8);
        AddOperationHistory("安装", "导出结果", Path.GetFileName(file), "成功");
        ShowNotice($"安装结果已导出到：{file}", "info");
    }

    private async void AppForceStop_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedManagedApp(out var app))
        {
            ShowNotice("请先选择一个应用。", "warn");
            return;
        }

        SetBusy(true, "正在强制停止应用...");
        try
        {
            if (!await EnsureOperationDeviceReadyAsync()) return;
            var result = await RunAdb($"shell am force-stop {app.PackageName}");
            if (result.ExitCode == 0)
            {
                AddOperationHistory("应用管理", "强制停止", app.PackageName, "成功");
                ShowNotice("已发送强制停止命令。", "info");
            }
            else
            {
                AddOperationHistory("应用管理", "强制停止", app.PackageName, "失败");
                ShowNotice("强制停止失败，请查看日志。", "warn");
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void AppClearData_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedManagedApp(out var app))
        {
            ShowNotice("请先选择一个应用。", "warn");
            return;
        }

        var confirm = MessageBox.Show($"确认清除以下应用的数据？\n\n{app.PackageName}", "确认清除数据", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true, "正在清除应用数据...");
        try
        {
            if (!await EnsureOperationDeviceReadyAsync()) return;
            var result = await RunAdb($"shell pm clear {app.PackageName}");
            var combined = $"{result.StdOut}\n{result.StdErr}";
            var success = result.ExitCode == 0 && combined.Contains("Success", StringComparison.OrdinalIgnoreCase);
            AddOperationHistory("应用管理", "清除数据", app.PackageName, success ? "成功" : "失败");
            ShowNotice(success ? "应用数据已清除。" : "清除数据失败，请查看日志。", success ? "info" : "warn");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void AppExportApk_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedManagedApp(out var app))
        {
            ShowNotice("请先选择一个应用。", "warn");
            return;
        }

        SetBusy(true, "正在导出 APK...");
        try
        {
            if (!await EnsureOperationDeviceReadyAsync()) return;
            var pathResult = await RunAdb($"shell pm path {app.PackageName}", logOutput: false);
            var remotePath = pathResult.StdOut.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .FirstOrDefault(x => x.StartsWith("package:", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(remotePath))
            {
                AddOperationHistory("应用管理", "导出 APK", app.PackageName, "失败");
                ShowNotice("未读取到 APK 路径。", "warn");
                return;
            }

            var cleanRemotePath = remotePath["package:".Length..].Trim();
            var dir = Path.Combine(EnsureMediaOutputDirectory(Environment.SpecialFolder.MyDocuments), "应用导出");
            Directory.CreateDirectory(dir);
            var localPath = Path.Combine(dir, SanitizeFileName(app.PackageName) + ".apk");
            var pull = await RunAdb($"pull {cleanRemotePath} \"{localPath}\"");
            var success = pull.ExitCode == 0;
            AddOperationHistory("应用管理", "导出 APK", app.PackageName, success ? "成功" : "失败");
            ShowNotice(success ? $"APK 已导出到：{localPath}" : "导出失败，请查看日志。", success ? "info" : "warn");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void AppOpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedManagedApp(out var app))
        {
            ShowNotice("请先选择一个应用。", "warn");
            return;
        }

        SetBusy(true, "正在打开应用详情页...");
        try
        {
            if (!await EnsureOperationDeviceReadyAsync()) return;
            var result = await RunAdb($"shell am start -a android.settings.APPLICATION_DETAILS_SETTINGS -d package:{app.PackageName}");
            var success = result.ExitCode == 0 && !result.StdErr.Contains("Exception", StringComparison.OrdinalIgnoreCase);
            AddOperationHistory("应用管理", "打开详情页", app.PackageName, success ? "成功" : "失败");
            ShowNotice(success ? "已尝试打开应用详情页。" : "打开应用详情页失败，请查看日志。", success ? "info" : "warn");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void AppReadPermissions_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedManagedApp(out var app))
        {
            ShowNotice("请先选择一个应用。", "warn");
            return;
        }

        SetBusy(true, "正在读取权限信息...");
        try
        {
            if (!await EnsureOperationDeviceReadyAsync()) return;
            var result = await RunAdb($"shell dumpsys package {app.PackageName}", logOutput: false);
            var permissions = ParseRequestedPermissions(result.StdOut).ToList();
            if (permissions.Count == 0)
            {
                ShowNotice("未读取到权限信息。", "warn");
                return;
            }

            var summary = string.Join("\n", permissions.Take(12));
            if (permissions.Count > 12) summary += $"\n... 其余 {permissions.Count - 12} 项";
            AppSelectedPackageText.Text += $"\n权限信息：\n{summary}";
            AddOperationHistory("应用管理", "查看权限", app.PackageName, $"{permissions.Count} 项");
            ShowNotice($"已读取 {permissions.Count} 项权限。", "info");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static IEnumerable<string> ParseRequestedPermissions(string output)
    {
        var lines = (output ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var inRequested = false;
        foreach (var line in lines)
        {
            var raw = line.Trim();
            if (raw.StartsWith("requested permissions:", StringComparison.OrdinalIgnoreCase))
            {
                inRequested = true;
                continue;
            }

            if (inRequested)
            {
                if (string.IsNullOrWhiteSpace(raw)) yield break;
                if (!raw.StartsWith("android.permission.", StringComparison.OrdinalIgnoreCase) && !raw.Contains('.')) yield break;
                yield return raw;
            }
        }
    }

    private async Task<bool> EnsureOperationDeviceReadyAsync()
    {
        if (!EnsureAdbExists()) return false;
        var state = await GetDeviceState();
        return GuardDeviceState(state);
    }

    private void ResetExtendedApkPreview()
    {
        ApkInfoSdkText.Text = "SDK：-";
        ApkInfoAbiText.Text = "ABI：-";
        ApkInfoSignatureText.Text = "签名：-";
    }

    private void BeginLoadApkManifestPreview(string apkPath)
    {
        if (!ValidateApkPath(apkPath))
        {
            ResetExtendedApkPreview();
            return;
        }

        ApkInfoSdkText.Text = "SDK：读取中...";
        ApkInfoAbiText.Text = "ABI：读取中...";
        ApkInfoSignatureText.Text = "签名：读取中...";
        _ = LoadApkManifestPreviewAsync(apkPath);
    }

    private async Task LoadApkManifestPreviewAsync(string apkPath)
    {
        try
        {
            var info = await Task.Run(() => ParseApkManifestInfo(apkPath));
            if (!string.Equals(ApkPathBox.Text?.Trim(), apkPath, StringComparison.OrdinalIgnoreCase)) return;

            ApkInfoPackageText.Text = $"包名：{info.PackageName}";
            ApkInfoVersionText.Text = $"版本：{info.VersionName}（版本号 {info.VersionCode}）";
            ApkInfoSdkText.Text = $"SDK：min {info.MinSdk} / target {info.TargetSdk}";
            ApkInfoAbiText.Text = $"ABI：{info.AbiSummary}";
            ApkInfoSignatureText.Text = $"签名：{info.SignatureSummary}";
        }
        catch (Exception ex)
        {
            if (!string.Equals(ApkPathBox.Text?.Trim(), apkPath, StringComparison.OrdinalIgnoreCase)) return;
            ApkInfoPackageText.Text = "包名：读取失败";
            ApkInfoVersionText.Text = "版本：读取失败";
            ApkInfoSdkText.Text = "SDK：读取失败";
            ApkInfoAbiText.Text = "ABI：读取失败";
            ApkInfoSignatureText.Text = $"签名：读取失败（{ex.Message}）";
        }
    }

    private static ApkManifestInfo ParseApkManifestInfo(string apkPath)
    {
        using var archive = ZipFile.OpenRead(apkPath);
        var manifestEntry = archive.GetEntry("AndroidManifest.xml") ?? throw new InvalidDataException("未找到 AndroidManifest.xml");
        using var manifestStream = manifestEntry.Open();
        using var ms = new MemoryStream();
        manifestStream.CopyTo(ms);
        var parser = new BinaryAndroidManifestParser(ms.ToArray());
        var info = parser.Parse();

        var abiEntries = archive.Entries
            .Select(e => e.FullName.Replace('\\', '/'))
            .Where(x => x.StartsWith("lib/", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Split('/'))
            .Where(x => x.Length >= 3)
            .Select(x => x[1])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        info.AbiSummary = abiEntries.Count == 0 ? "无原生库 / 通用包" : string.Join(", ", abiEntries);
        info.SignatureSummary = ReadApkSignatureSummary(archive);
        return info;
    }

    private static string ReadApkSignatureSummary(ZipArchive archive)
    {
        var entry = archive.Entries.FirstOrDefault(e => Regex.IsMatch(e.FullName, @"^META-INF/.*\.(RSA|DSA|EC)$", RegexOptions.IgnoreCase));
        if (entry == null) return "未找到证书文件";

        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        try
        {
            var signedCms = new SignedCms();
            signedCms.Decode(ms.ToArray());
            var cert = signedCms.Certificates.Cast<X509Certificate2>().FirstOrDefault();
            if (cert == null) return $"证书文件：{entry.Name}";
            var sha = SHA256.HashData(cert.RawData);
            var hex = Convert.ToHexString(sha);
            return $"SHA-256 {string.Join(':', Enumerable.Range(0, Math.Min(16, hex.Length / 2)).Select(i => hex.Substring(i * 2, 2)))}";
        }
        catch
        {
            var sha = SHA256.HashData(ms.ToArray());
            return $"证书哈希：{Convert.ToHexString(sha)[..16]}";
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private sealed class BinaryAndroidManifestParser
    {
        private readonly byte[] _data;
        private readonly List<string> _strings = new();

        public BinaryAndroidManifestParser(byte[] data)
        {
            _data = data;
        }

        public ApkManifestInfo Parse()
        {
            ParseStringPool();
            var info = new ApkManifestInfo();
            var offset = 8;
            while (offset + 8 <= _data.Length)
            {
                var chunkType = ReadUInt16(offset);
                var chunkSize = (int)ReadUInt32(offset + 4);
                if (chunkSize <= 0 || offset + chunkSize > _data.Length) break;

                if (chunkType == 0x0102)
                {
                    ParseStartElement(offset, info);
                }

                offset += chunkSize;
            }

            return info;
        }

        private void ParseStringPool()
        {
            if (_data.Length < 36 || ReadUInt16(8) != 0x0001) return;
            const int chunkOffset = 8;
            var stringCount = (int)ReadUInt32(chunkOffset + 8);
            var flags = ReadUInt32(chunkOffset + 16);
            var stringsStart = (int)ReadUInt32(chunkOffset + 20);
            var isUtf8 = (flags & 0x00000100) != 0;

            for (var i = 0; i < stringCount; i++)
            {
                var stringOffset = (int)ReadUInt32(chunkOffset + 28 + (i * 4));
                var absoluteOffset = chunkOffset + stringsStart + stringOffset;
                _strings.Add(isUtf8 ? ReadUtf8String(absoluteOffset) : ReadUtf16String(absoluteOffset));
            }
        }

        private void ParseStartElement(int offset, ApkManifestInfo info)
        {
            var nameIndex = (int)ReadUInt32(offset + 20);
            var attributeStart = ReadUInt16(offset + 28);
            var attributeSize = ReadUInt16(offset + 30);
            var attributeCount = ReadUInt16(offset + 32);
            var tagName = GetString(nameIndex);
            var attrOffset = offset + attributeStart;

            for (var i = 0; i < attributeCount; i++)
            {
                var current = attrOffset + (i * attributeSize);
                var attrName = GetString((int)ReadUInt32(current + 4));
                var rawValueIndex = unchecked((int)ReadUInt32(current + 8));
                var typedValueType = _data[current + 15];
                var typedValueData = ReadUInt32(current + 16);
                var value = rawValueIndex == -1 ? ConvertTypedValue(typedValueType, typedValueData) : GetString(rawValueIndex);

                if (tagName == "manifest")
                {
                    if (attrName == "package") info.PackageName = value;
                    if (attrName == "versionName") info.VersionName = value;
                    if (attrName == "versionCode") info.VersionCode = value;
                }
                else if (tagName == "uses-sdk")
                {
                    if (attrName == "minSdkVersion") info.MinSdk = value;
                    if (attrName == "targetSdkVersion") info.TargetSdk = value;
                }
            }
        }

        private string ConvertTypedValue(byte valueType, uint data)
        {
            return valueType switch
            {
                0x03 => GetString((int)data),
                0x10 => data.ToString(),
                0x12 => data != 0 ? "是" : "否",
                _ => data.ToString()
            };
        }

        private string GetString(int index)
        {
            if (index < 0 || index >= _strings.Count) return "-";
            return _strings[index];
        }

        private ushort ReadUInt16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(offset, 2));
        private uint ReadUInt32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset, 4));

        private string ReadUtf8String(int offset)
        {
            var index = offset;
            _ = ReadLength8(ref index);
            var byteLength = ReadLength8(ref index);
            return Encoding.UTF8.GetString(_data, index, byteLength);
        }

        private int ReadLength8(ref int offset)
        {
            var len = (int)_data[offset++];
            if ((len & 0x80) != 0)
            {
                len = ((len & 0x7F) << 8) | _data[offset++];
            }
            return len;
        }

        private string ReadUtf16String(int offset)
        {
            var len = ReadLength16(ref offset);
            return Encoding.Unicode.GetString(_data, offset, len * 2);
        }

        private int ReadLength16(ref int offset)
        {
            var len = (int)ReadUInt16(offset);
            offset += 2;
            if ((len & 0x8000) != 0)
            {
                len = ((len & 0x7FFF) << 16) | ReadUInt16(offset);
                offset += 2;
            }
            return len;
        }
    }
}
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
namespace QuestADBTool;
public partial class MainWindow
{

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

}
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;

namespace QuestADBTool.Views;

public partial class InstallPage : UserControl
{
    public InstallPage()
    {
        InitializeComponent();
    }

    public Border InstallCardBorderControl => InstallCardBorder;
    public ColumnDefinition InstallLeftColumnControl => InstallLeftColumn;
    public ColumnDefinition InstallRightColumnControl => InstallRightColumn;

    public TextBlock InstallSuccessBadgeControl => InstallSuccessBadge;
    public TextBlock InstallFailBadgeControl => InstallFailBadge;
    public Ellipse InstallDeviceStateDotControl => InstallDeviceStateDot;
    public TextBlock InstallDeviceStateTextControl => InstallDeviceStateText;
    public Button InstallHeaderRefreshButtonControl => InstallHeaderRefreshButton;

    public TextBox ApkPathBoxControl => ApkPathBox;
    public Button PickApkButtonControl => PickApkButton;
    public Button InstallApkButtonControl => InstallApkButton;

    public TextBox InputTextBoxControl => InputTextBox;
    public Button SendTextButtonControl => SendTextButton;

    public TextBlock ApkInfoFileTextControl => ApkInfoFileText;
    public TextBlock ApkInfoSizeTextControl => ApkInfoSizeText;
    public TextBlock ApkInfoPackageTextControl => ApkInfoPackageText;
    public TextBlock ApkInfoVersionTextControl => ApkInfoVersionText;
    public TextBlock ApkInfoSdkTextControl => ApkInfoSdkText;
    public TextBlock ApkInfoAbiTextControl => ApkInfoAbiText;
    public TextBlock ApkInfoSignatureTextControl => ApkInfoSignatureText;

    public Expander AdvancedOptionsExpanderControl => AdvancedOptionsExpander;
    public CheckBox ReplaceInstallCheckBoxControl => ReplaceInstallCheckBox;
    public CheckBox AllowDowngradeCheckBoxControl => AllowDowngradeCheckBox;
    public CheckBox AllowTestApkCheckBoxControl => AllowTestApkCheckBox;

    public TextBlock QueueCountTextControl => QueueCountText;
    public ListBox QueueListBoxControl => QueueListBox;

    public Button AddQueueButtonControl => AddQueueButton;
    public Button StartQueueButtonControl => StartQueueButton;
    public Button PauseQueueButtonControl => PauseQueueButton;
    public Button StopQueueButtonControl => StopQueueButton;
    public Button ClearQueueButtonControl => ClearQueueButton;
    public Button RetryFailedButtonControl => RetryFailedButton;
    public Button ExportQueueResultButtonControl => ExportQueueResultButton;
    public Button RemoveSelectedButtonControl => RemoveSelectedButton;

    public Border InstallBusyOverlayControl => InstallBusyOverlay;
    public TextBlock InstallOverlayTextControl => InstallOverlayText;
    public ProgressBar InstallOverlayProgressBarControl => InstallOverlayProgressBar;
    public TextBlock InstallOverlayElapsedTextControl => InstallOverlayElapsedText;
}
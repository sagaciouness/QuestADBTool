using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows;

namespace QuestADBTool.Views;

public partial class DevicePage : UserControl
{
    public DevicePage()
    {
        InitializeComponent();
    }

    public Ellipse StatusDotControl => StatusDot;
    public TextBlock StatusTextControl => StatusText;
    public TextBlock StatusHintTextControl => StatusHintText;
    public Button RefreshButtonControl => RefreshButton;
    public Button RestartButtonControl => RestartButton;
    public Button GuideButtonControl => GuideButton;
    public Button OpenLogButtonControl => OpenLogButton;
    public Button CheckUpdateButtonControl => CheckUpdateButton;
    public Button RunDiagnosticsButtonControl => RunDiagnosticsButton;
    public Button CopyDiagnosticsButtonControl => CopyDiagnosticsButton;
    public TextBox DiagnosticsBoxControl => DiagnosticsBox;

    public ScrollViewer DeviceInfoScrollViewerControl => DeviceInfoScrollViewer;
    public Button DeviceInfoScrollLeftButtonControl => DeviceInfoScrollLeftButton;
    public Button DeviceInfoScrollRightButtonControl => DeviceInfoScrollRightButton;

    public TextBlock DeviceSerialTextControl => DeviceSerialText;
    public TextBlock DeviceModelTextControl => DeviceModelText;
    public TextBlock DeviceAndroidTextControl => DeviceAndroidText;
    public TextBlock DeviceBatteryTextControl => DeviceBatteryText;
    public TextBlock DeviceStorageTextControl => DeviceStorageText;

    public Button ExpCaptureScreenButtonControl => ExpCaptureScreenButton;
    public Button ExpStartRecordButtonControl => ExpStartRecordButton;
    public Button ExpStopRecordButtonControl => ExpStopRecordButton;
    public TextBlock ExpRecordStateTextControl => ExpRecordStateText;

    public TextBox ExpAutomationScriptBoxControl => ExpAutomationScriptBox;
    public Button ExpRunAutomationButtonControl => ExpRunAutomationButton;
    public Button ExpFillAutomationTemplateButtonControl => ExpFillAutomationTemplateButton;
}
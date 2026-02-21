using System.Windows.Controls;

namespace QuestADBTool.Views;

public partial class AppManagePage : UserControl
{
    public AppManagePage()
    {
        InitializeComponent();
    }

    public Button AppRefreshButtonControl => AppRefreshButton;
    public CheckBox AppIncludeSystemCheckBoxControl => AppIncludeSystemCheckBox;
    public TextBox AppSearchBoxControl => AppSearchBox;
    public TextBlock AppCountTextControl => AppCountText;
    public StackPanel AppRefreshProgressPanelControl => AppRefreshProgressPanel;
    public TextBlock AppRefreshProgressTextControl => AppRefreshProgressText;
    public TextBlock AppRefreshProgressValueTextControl => AppRefreshProgressValueText;
    public ProgressBar AppRefreshProgressBarControl => AppRefreshProgressBar;
    public ListBox AppListBoxControl => AppListBox;
    public TextBlock AppSelectedPackageTextControl => AppSelectedPackageText;
    public Button AppLaunchButtonControl => AppLaunchButton;
    public Button AppUninstallButtonControl => AppUninstallButton;
    public Button AppCopyPackageButtonControl => AppCopyPackageButton;
}

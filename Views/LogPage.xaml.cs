using System.Windows.Controls;

namespace QuestADBTool.Views;

public partial class LogPage : UserControl
{
    public LogPage()
    {
        InitializeComponent();
    }

    public Button ToggleLogButtonControl => ToggleLogButton;
    public StackPanel LogBodyPanelControl => LogBodyPanel;
    public Button AuthorButtonControl => AuthorButton;
    public Button ClearLogButtonControl => ClearLogButton;
    public Button CopyLogButtonControl => CopyLogButton;
    public TextBox LogBoxControl => LogBox;
}

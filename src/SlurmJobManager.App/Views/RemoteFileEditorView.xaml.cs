using System.Windows;
using SlurmJobManager.App.ViewModels;

namespace SlurmJobManager.App.Views;

public partial class RemoteFileEditorView : Window
{
    public RemoteFileEditorView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Editor.TextArea.TextView.CurrentLineBackground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(80, 67, 80, 108));
            Editor.TextArea.TextView.CurrentLineBorder = null;
            Editor.LineNumbersForeground = (System.Windows.Media.Brush?)FindResource("TextMutedBrush");
            Editor.Options.HighlightCurrentLine = true;
        };
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

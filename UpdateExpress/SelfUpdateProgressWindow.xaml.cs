using System.Windows;

namespace UpdateExpress;

public partial class SelfUpdateProgressWindow : Window
{
    public SelfUpdateProgressWindow()
    {
        InitializeComponent();
        AppIcon.Apply(this);
    }

    public void SetStage(string text)
    {
        StageText.Text = text;
    }
}

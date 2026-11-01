using System.Windows;
using System.Windows.Input;
using BDM.App.Services;
using BDM.Core.Streams;

namespace BDM.App.Views;

public partial class QualityPickerDialog : Window
{
    public StreamChoice? Selected { get; private set; }

    public QualityPickerDialog(IReadOnlyList<StreamChoice> choices, string title)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        Icon = AppIcon.Image();
        Subtitle.Text = title;
        List.ItemsSource = choices;
        List.SelectedIndex = 0; // best quality first
        Loaded += (_, _) => List.Focus();
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        Selected = List.SelectedItem as StreamChoice;
        DialogResult = Selected != null;
    }

    void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem != null) Ok_Click(sender, e);
    }
}

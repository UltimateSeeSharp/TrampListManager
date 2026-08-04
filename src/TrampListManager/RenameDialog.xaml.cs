using System.Windows;

namespace TrampListManager;

public partial class RenameDialog : Window
{
    public string DesignName => NameBox.Text.Trim();
    public string? Notes => string.IsNullOrWhiteSpace(NotesBox.Text) ? null : NotesBox.Text.Trim();

    public RenameDialog(string? currentName, string? currentNotes)
    {
        InitializeComponent();
        NameBox.Text = currentName ?? "";
        NotesBox.Text = currentNotes ?? "";
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}

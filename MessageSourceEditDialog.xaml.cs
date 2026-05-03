using System.Windows;
using System.Windows.Controls;

namespace InfoTransfer;

public partial class MessageSourceEditDialog : Window
{
    public string SourceId => TxtSourceId.Text.Trim();
    public string SourceName => TxtName.Text.Trim();
    public string ApiMethod => (CmbApiMethod.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "GET";
    public string ApiUrl => TxtApiUrl.Text.Trim();
    public string ApiParameters => TxtApiParameters.Text.Trim();
    public string ApiToken => TxtApiToken.Text.Trim();
    public string ResponseFormat => TxtResponseFormat.Text.Trim();
    public string Description => TxtDescription.Text.Trim();

    public MessageSourceEditDialog()
    {
        InitializeComponent();
    }

    private void BtnOK_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SourceId))
        {
            MessageBox.Show("请输入 Source ID", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtSourceId.Focus();
            return;
        }

        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

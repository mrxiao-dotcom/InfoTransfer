using System.Windows;
using System.Windows.Controls;

namespace InfoTransfer;

public partial class AccountEditDialog : Window
{
    public string AccountId => TxtAccountId.Text.Trim();
    public string AccountName => TxtName.Text.Trim();
    public string AccountType => (CmbAccountType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Feishu";
    public string Credentials => TxtCredentials.Text.Trim();
    public string Description => TxtDescription.Text.Trim();

    public AccountEditDialog()
    {
        InitializeComponent();
    }

    private void BtnOK_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AccountId))
        {
            MessageBox.Show("请输入 Account ID", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtAccountId.Focus();
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

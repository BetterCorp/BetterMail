using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BetterMail.Core;

namespace BetterMail.App;

internal sealed class DatabaseUnlockWindow : Window
{
    public string? Key { get; private set; }

    public DatabaseUnlockWindow(string dataDirectory, Exception initialError)
    {
        Title = "Unlock BetterMail";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var recovery = initialError is DatabaseKeyRecoveryException;
        var message = new TextBlock { Text = initialError.Message, TextWrapping = TextWrapping.Wrap };
        var password = new TextBox
        {
            PasswordChar = '●', PlaceholderText = "Previous database password", IsVisible = recovery
        };
        AutomationProperties.SetName(password, "Previous database password");
        var submit = new Button { Content = recovery ? "Unlock and remember" : "Try again", IsDefault = true };
        var cancel = new Button { Content = "Quit", IsCancel = true };
        var working = false;
        cancel.Click += (_, _) => Close();
        Closing += (_, args) => args.Cancel = working;
        submit.Click += async (_, _) =>
        {
            if (working) return;
            if (recovery && string.IsNullOrWhiteSpace(password.Text))
            {
                message.Text = "Enter the password used by your previous BetterMail setup.";
                password.Focus();
                return;
            }
            working = true;
            submit.IsEnabled = cancel.IsEnabled = password.IsEnabled = false;
            var passphrase = password.Text ?? "";
            password.Text = "";
            try
            {
                var key = await Task.Run(() => recovery
                    ? DatabaseKeyProvider.Recover(dataDirectory, passphrase)
                    : DatabaseKeyProvider.GetOrCreate(dataDirectory));
                working = false;
                Key = key;
                Close();
            }
            catch (Exception error)
            {
                if (error is DatabaseKeyRecoveryException)
                {
                    recovery = true;
                    password.IsVisible = true;
                    submit.Content = "Unlock and remember";
                }
                message.Text = error.Message;
            }
            finally
            {
                working = false;
                submit.IsEnabled = cancel.IsEnabled = password.IsEnabled = true;
            }
        };
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24), Spacing = 16,
            Children =
            {
                new TextBlock { Text = "Unlock your local mail", FontSize = 22 },
                message, password,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 12, Children = { cancel, submit }
                }
            }
        };
        Opened += (_, _) => { if (recovery) password.Focus(); else submit.Focus(); };
    }
}

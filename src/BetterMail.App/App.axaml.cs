using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using BetterMail.Core;

namespace BetterMail.App;

public sealed partial class App : Application
{
    private EncryptedMailStore? _store;
    private AppUpdater? _updater;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BetterMail");
            var preferences = AppPreferencesStore.Load(dataDirectory);
            ApplyTheme(preferences.ThemeMode);
            var startupWindow = new StartupWindow(preferences.ThemeMode == "Dark");
            desktop.MainWindow = startupWindow;
            var started = false;
            startupWindow.Opened += async (_, _) =>
            {
                if (started)
                {
                    return;
                }
                started = true;
                await Task.Delay(50);
                await StartMainWindowAsync(desktop, startupWindow, dataDirectory, preferences);
            };
            desktop.Exit += async (_, _) =>
            {
                _updater?.Dispose();
                await DisposeStoreAsync();
            };

        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartMainWindowAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        StartupWindow startupWindow,
        string dataDirectory,
        AppPreferences preferences)
    {
        string? startupError = null;
        try
        {
            var key = DatabaseKeyProvider.GetOrCreate(dataDirectory);
            _store = new EncryptedMailStore(Path.Combine(dataDirectory, "mail.db"), key);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            startupError = exception.Message;
        }

        MainWindow? mainWindow = null;
        var notificationService = DesktopNotificationServices.Create(
            () => mainWindow?.TryGetPlatformHandle()?.Handle ?? 0);
        var viewModel = new MainWindowViewModel(
            _store,
            dataDirectory,
            ApplyTheme,
            ApplyAccent,
            startupError,
            desktopNotificationService: notificationService);
        viewModel.SelectedThemeMode = preferences.ThemeMode;
        viewModel.SelectedAccentName = preferences.AccentName;
        viewModel.IsCompact = preferences.IsCompact;
        viewModel.DesktopNotificationsEnabled = preferences.DesktopNotificationsEnabled;
        viewModel.MailSyncRange = preferences.MailSyncRange;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainWindowViewModel.SelectedThemeMode) or
                nameof(MainWindowViewModel.SelectedAccentName) or
                nameof(MainWindowViewModel.IsCompact) or
                nameof(MainWindowViewModel.DesktopNotificationsEnabled) or
                nameof(MainWindowViewModel.MailSyncRange) or
                nameof(MainWindowViewModel.SenderPreferencesVersion))
            {
                AppPreferencesStore.Save(dataDirectory, new AppPreferences(
                    ThemeMode: viewModel.SelectedThemeMode,
                    AccentName: viewModel.SelectedAccentName,
                    IsCompact: viewModel.IsCompact,
                    DesktopNotificationsEnabled: viewModel.DesktopNotificationsEnabled,
                    MailSyncRange: viewModel.MailSyncRange,
                    DefaultSenderMailboxId: viewModel.DefaultSenderMailboxId,
                    Signatures: viewModel.GetSignaturePreferences(),
                    MailboxSignatures: viewModel.GetMailboxSignaturePreferences()));
            }
        };
        viewModel.ConfigureSenderPreferences(
            preferences.Signature,
            preferences.DefaultSenderMailboxId,
            preferences.SenderSignatures,
            preferences.Signatures,
            preferences.MailboxSignatures);
        mainWindow = new MainWindow { DataContext = viewModel };
        desktop.MainWindow = mainWindow;
        mainWindow.Show();
        startupWindow.Close();
        await viewModel.InitializeAsync();
        await mainWindow.RestorePreviewWindowsAsync();

        _updater = AppUpdater.Create(async () =>
        {
            await DisposeStoreAsync();
            desktop.Shutdown();
        });
        if (_updater is not null)
        {
            var updater = _updater;
            mainWindow.CheckForUpdatesAsync = () => updater.CheckNowAsync(mainWindow);
            await _updater.StartAsync(mainWindow);
        }
    }

    private async Task DisposeStoreAsync()
    {
        var store = Interlocked.Exchange(ref _store, null);
        if (store is not null)
        {
            await store.DisposeAsync();
        }
    }

    private void ApplyTheme(string mode)
    {
        RequestedThemeVariant = mode switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    private void ApplyAccent(string color)
    {
        if (Color.TryParse(color, out var parsed))
        {
            Resources["BetterMailAccentColor"] = parsed;
        }
    }
}

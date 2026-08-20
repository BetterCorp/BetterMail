using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.Styling;
using BetterMail.Core;

namespace BetterMail.App;

public sealed partial class App : Application
{
    private EncryptedMailStore? _store;
    private AppUpdater? _updater;
    private IDesktopNotificationService? _desktopNotificationService;
    private MainWindow? _mainWindow;
    private MainWindowViewModel? _viewModel;
    private bool _ready;
    private bool _showingDefaultMailPrompt;
    private readonly Queue<string> _pendingActivations = [];

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        AppActivation.SetHandler(value => Dispatcher.UIThread.Post(() => _ = HandleActivationAsync(value)));
        if (this.TryGetFeature<IActivatableLifetime>() is { } activatableLifetime)
        {
            activatableLifetime.Activated += (_, args) =>
            {
                if (args.Kind == ActivationKind.OpenUri &&
                    args.GetType().GetProperty("Uri")?.GetValue(args) is Uri uri)
                {
                    AppActivation.Publish(uri.AbsoluteUri);
                }
            };
        }
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
                (_desktopNotificationService as IDisposable)?.Dispose();
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
        MainWindowViewModel? viewModel = null;
        _desktopNotificationService = DesktopNotificationServices.Create(
            () => mainWindow?.TryGetPlatformHandle()?.Handle ?? 0,
            (mailboxId, folderId, messageId) => Dispatcher.UIThread.Post(async () =>
            {
                if (viewModel is not null)
                {
                    try
                    {
                        if (mainWindow is not null)
                        {
                            await mainWindow.OpenNotificationAsync(mailboxId, folderId, messageId);
                        }
                    }
                    catch (Exception exception)
                    {
                        viewModel.ReportError($"Notification could not be opened: {exception.Message}");
                    }
                }
            }));
        viewModel = new MainWindowViewModel(
            _store,
            dataDirectory,
            ApplyTheme,
            ApplyAccent,
            startupError,
            desktopNotificationService: _desktopNotificationService);
        viewModel.SelectedThemeMode = preferences.ThemeMode;
        viewModel.SelectedAccentName = preferences.AccentName;
        viewModel.IsCompact = preferences.IsCompact;
        viewModel.DesktopNotificationsEnabled = preferences.DesktopNotificationsEnabled;
        viewModel.DefaultMailPromptShown = preferences.DefaultMailPromptShown;
        viewModel.MailSyncRange = preferences.MailSyncRange;
        viewModel.ConfigureMailQuickActions(preferences.MailQuickActions);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainWindowViewModel.SelectedThemeMode) or
                nameof(MainWindowViewModel.SelectedAccentName) or
                nameof(MainWindowViewModel.IsCompact) or
                nameof(MainWindowViewModel.DesktopNotificationsEnabled) or
                nameof(MainWindowViewModel.DefaultMailPromptShown) or
                nameof(MainWindowViewModel.MailSyncRange) or
                nameof(MainWindowViewModel.MailQuickActionsVersion) or
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
                    MailboxSignatures: viewModel.GetMailboxSignaturePreferences(),
                    MailQuickActions: viewModel.GetMailQuickActionPreferences(),
                    DefaultMailPromptShown: viewModel.DefaultMailPromptShown));
            }
        };
        viewModel.ConfigureSenderPreferences(
            preferences.Signature,
            preferences.DefaultSenderMailboxId,
            preferences.SenderSignatures,
            preferences.Signatures,
            preferences.MailboxSignatures);
        mainWindow = new MainWindow { DataContext = viewModel };
        _mainWindow = mainWindow;
        _viewModel = viewModel;
        desktop.MainWindow = mainWindow;
        mainWindow.Show();
        startupWindow.Close();
        await viewModel.InitializeAsync();
        await mainWindow.RestorePreviewWindowsAsync();
        _ready = true;
        while (_pendingActivations.TryDequeue(out var activation))
        {
            await HandleActivationAsync(activation);
        }
        viewModel.Accounts.CollectionChanged += (_, _) =>
            Dispatcher.UIThread.Post(() => _ = ShowDefaultMailPromptAsync());
        await ShowDefaultMailPromptAsync();

        _updater = AppUpdater.Create(async () =>
        {
            await DisposeStoreAsync();
            desktop.Shutdown();
        });
        if (_updater is not null)
        {
            var updater = _updater;
            mainWindow.CheckForUpdatesAsync = updater.CheckNowAsync;
            await _updater.StartAsync();
        }
    }

    private async Task HandleActivationAsync(string activation)
    {
        if (!_ready || _mainWindow is null || _viewModel is null)
        {
            _pendingActivations.Enqueue(activation);
            return;
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }
        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }
        _mainWindow.Activate();
        if (MailtoParser.TryParse(activation, out var request))
        {
            await _viewModel.OpenComposeAsync(request);
        }
    }

    private async Task ShowDefaultMailPromptAsync()
    {
        var viewModel = _viewModel;
        var owner = _mainWindow;
        if (!_ready || owner is null || viewModel is null || viewModel.Accounts.Count == 0 ||
            viewModel.DefaultMailPromptShown || _showingDefaultMailPrompt)
        {
            return;
        }

        _showingDefaultMailPrompt = true;
        viewModel.DefaultMailPromptShown = true;
        var choose = new Button { Content = "Choose default mail app" };
        var later = new Button { Content = "Not now" };
        var dialog = new Window
        {
            Title = "Open mail links with BetterMail?",
            Icon = owner.Icon,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(22),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = "BetterMail can create a new message when you click a mail link.",
                        FontSize = 18,
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = OperatingSystem.IsMacOS()
                            ? "We’ll open Mail. Choose BetterMail under Mail > Settings > General > Default email reader."
                            : "Your operating system will confirm which app should open mail links.",
                        Opacity = 0.7,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { later, choose }
                    }
                }
            }
        };
        later.Click += (_, _) => dialog.Close();
        choose.Click += async (_, _) =>
        {
            await ((AsyncCommand)viewModel.ChooseDefaultMailAppCommand).ExecuteAsync();
            dialog.Close();
        };
        try
        {
            await IndependentWindow.ShowAsync(dialog);
        }
        finally
        {
            _showingDefaultMailPrompt = false;
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
            Resources["BetterMailQuickActionShadow"] = new BoxShadows(new BoxShadow
            {
                OffsetX = -8,
                Blur = 14,
                Spread = -3,
                Color = Color.FromArgb(0x99, parsed.R, parsed.G, parsed.B)
            });
        }
    }
}

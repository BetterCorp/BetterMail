using System.Diagnostics;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BetterMail.Core;
using System.Windows.Input;

namespace BetterMail.App;

public sealed partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;
    private ResponsiveLayoutMode _layoutMode;
    private bool _showFolders;
    private bool _showPhoneMessage;
    private PointerPressedEventArgs? _mailDragStart;
    private Point _mailDragOrigin;
    private IReadOnlyList<MailMessage> _draggedMessages = [];
    private Vector? _messageScrollOffsetBeforeSync;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindViewModel(DataContext as MainWindowViewModel);
        SizeChanged += (_, args) => ApplyResponsiveLayout(args.NewSize.Width);
        KeyDown += MainWindowKeyDown;
        ApplyResponsiveLayout(Width);
    }

    internal static ResponsiveLayoutMode LayoutModeFor(double width) => width switch
    {
        < 720 => ResponsiveLayoutMode.Phone,
        < 1200 => ResponsiveLayoutMode.Compact,
        _ => ResponsiveLayoutMode.Wide
    };

    internal static bool UsesInlineMailActions(double width) => width >= 840;

    internal static ShellKeyAction ShellActionFor(
        Key key,
        bool isMailModule,
        bool isSettingsOpen,
        bool hasMailBackStage,
        bool isTextInput)
    {
        if (key == Key.Escape && isSettingsOpen)
        {
            return ShellKeyAction.CloseSettings;
        }
        if (!isMailModule || isSettingsOpen)
        {
            return ShellKeyAction.None;
        }
        if (key == Key.Escape && hasMailBackStage)
        {
            return ShellKeyAction.BackToMessageList;
        }
        if (isTextInput)
        {
            return ShellKeyAction.None;
        }
        return key switch
        {
            Key.Back => ShellKeyAction.Archive,
            Key.Delete => ShellKeyAction.Delete,
            _ => ShellKeyAction.None
        };
    }

    private void ApplyResponsiveLayout(double width)
    {
        _layoutMode = LayoutModeFor(width);
        var phone = _layoutMode == ResponsiveLayoutMode.Phone;
        var wide = _layoutMode == ResponsiveLayoutMode.Wide;

        SetRows(ShellGrid, 44, 46, 1, phone ? 48 : 0);
        SetColumns(ShellGrid,
            phone ? 0 : 48,
            wide ? 248 : 0,
            phone ? 1 : wide ? 380 : 320,
            phone ? 0 : 1);

        Grid.SetRow(AppRail, phone ? 3 : 1);
        Grid.SetRowSpan(AppRail, phone ? 1 : 2);
        Grid.SetColumn(AppRail, 0);
        Grid.SetColumnSpan(AppRail, phone ? 4 : 1);
        AppRail.BorderThickness = phone ? new Thickness(0, 1, 0, 0) : new Thickness(0, 0, 1, 0);
        AppRailLayout.RowDefinitions.Clear();
        AppRailLayout.ColumnDefinitions.Clear();
        if (phone)
        {
            AppRailLayout.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            SetColumns(AppRailLayout, 1, GridLength.Auto);
            RailModules.Orientation = Orientation.Horizontal;
            Grid.SetRow(RailModules, 0);
            Grid.SetColumn(RailModules, 0);
            Grid.SetRow(RailSettings, 0);
            Grid.SetColumn(RailSettings, 1);
        }
        else
        {
            SetRows(AppRailLayout, GridLength.Auto, 1, GridLength.Auto);
            AppRailLayout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            RailModules.Orientation = Orientation.Vertical;
            Grid.SetRow(RailModules, 0);
            Grid.SetColumn(RailModules, 0);
            Grid.SetRow(RailSettings, 2);
            Grid.SetColumn(RailSettings, 0);
        }

        HeaderBrand.IsVisible = wide;
        SetColumns(HeaderLayout, 1, GridLength.Auto);
        Grid.SetColumn(HeaderSearch, 0);
        if (wide)
        {
            SetColumns(HeaderLayout, 230, 1, GridLength.Auto);
            Grid.SetColumn(HeaderSearch, 1);
        }

        Grid.SetColumn(FolderPane, wide ? 1 : 2);
        Grid.SetColumn(MessageListPane, 2);
        Grid.SetColumn(ReadingPane, phone ? 2 : 3);
        Grid.SetColumn(ErrorToast, phone ? 2 : 3);
        FolderToggle.IsVisible = !wide;
        InlineMailActions.IsVisible = UsesInlineMailActions(width);
        PhoneBack.IsVisible = phone;
        var showMail = _viewModel?.ShowMailSurface ?? true;
        FolderSplitter.IsVisible = showMail && wide;
        ReadingSplitter.IsVisible = showMail && !phone;

        SetColumns(ModuleHeader, phone ? 1 : 1, GridLength.Auto);
        Grid.SetRow(ModuleSearch, phone ? 1 : 0);
        Grid.SetColumn(ModuleSearch, 0);
        Grid.SetColumnSpan(ModuleSearch, phone ? 2 : 1);
        Grid.SetRow(ModuleRefresh, 0);
        Grid.SetColumn(ModuleRefresh, 1);
        if (!phone)
        {
            SetColumns(ModuleHeader, 1, 320, GridLength.Auto);
            Grid.SetColumn(ModuleSearch, 1);
            Grid.SetColumnSpan(ModuleSearch, 1);
            Grid.SetColumn(ModuleRefresh, 2);
        }

        SettingsContent.Margin = new Thickness(phone ? 16 : 28);
        GlobalSearchResultsPanel.Width = Math.Clamp(width - (phone ? 24 : 360), 300, 1000);
        UpdateMailPanes();
    }

    private void UpdateMailPanes()
    {
        var wide = _layoutMode == ResponsiveLayoutMode.Wide;
        var phone = _layoutMode == ResponsiveLayoutMode.Phone;
        var showMail = _viewModel?.ShowMailSurface ?? true;
        FolderPane.IsVisible = showMail && (wide || _showFolders);
        MessageListPane.IsVisible = showMail && (wide || (!_showFolders && (!phone || !_showPhoneMessage)));
        ReadingPane.IsVisible = showMail && (wide || (!phone && _layoutMode == ResponsiveLayoutMode.Compact) || (!_showFolders && _showPhoneMessage));
        FolderSplitter.IsVisible = showMail && wide;
        ReadingSplitter.IsVisible = showMail && !phone;
    }

    private static void SetColumns(Grid grid, params object[] lengths)
    {
        grid.ColumnDefinitions.Clear();
        foreach (var length in lengths)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(ToGridLength(length)));
        }
    }

    private static void SetRows(Grid grid, params object[] lengths)
    {
        grid.RowDefinitions.Clear();
        foreach (var length in lengths)
        {
            grid.RowDefinitions.Add(new RowDefinition(ToGridLength(length)));
        }
    }

    private static GridLength ToGridLength(object length) => length switch
    {
        GridLength gridLength => gridLength,
        int value when value == 1 => GridLength.Star,
        int value => new GridLength(value),
        _ => throw new ArgumentOutOfRangeException(nameof(length))
    };

    private void FolderToggleClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        _showFolders = !_showFolders;
        _showPhoneMessage = false;
        UpdateMailPanes();
    }

    private void FolderSelectedClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        _showFolders = false;
        _showPhoneMessage = false;
        UpdateMailPanes();
    }

    private void MailModuleClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        _showFolders = false;
        _showPhoneMessage = false;
        UpdateMailPanes();
    }

    private void MessageListPointerReleased(object? sender, PointerReleasedEventArgs args) => ShowPhoneMessage();

    private void MessageSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        _viewModel?.SetSelectedMessages(MessageList.SelectedItems?.OfType<MailMessage>() ?? []);
    }

    private void MessageDragPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (sender is not Border { DataContext: MailMessage message } ||
            !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        var selectedItems = MessageList.SelectedItems;
        if (selectedItems is null)
        {
            return;
        }
        if (!selectedItems.Contains(message))
        {
            selectedItems.Clear();
            selectedItems.Add(message);
        }
        _mailDragStart = args;
        _mailDragOrigin = args.GetPosition(this);
    }

    private async void MessageDragPointerMoved(object? sender, PointerEventArgs args)
    {
        if (_mailDragStart is null || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        var position = args.GetPosition(this);
        if (Math.Abs(position.X - _mailDragOrigin.X) < 6 && Math.Abs(position.Y - _mailDragOrigin.Y) < 6)
        {
            return;
        }

        var pressed = _mailDragStart;
        _mailDragStart = null;
        _draggedMessages = MessageList.SelectedItems?.OfType<MailMessage>().ToArray() ?? [];
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText("bettermail-mail"));
        await DragDrop.DoDragDropAsync(pressed, data, DragDropEffects.Move);
        _draggedMessages = [];
    }

    private void FolderDragOver(object? sender, DragEventArgs args)
    {
        args.DragEffects = sender is Button { DataContext: MailFolderNode node } &&
            _draggedMessages.Count > 0 &&
            _viewModel?.CanMoveSelectionToFolder(node.Item) == true
                ? DragDropEffects.Move
                : DragDropEffects.None;
    }

    private async void FolderDrop(object? sender, DragEventArgs args)
    {
        if (sender is Button { DataContext: MailFolderNode node } &&
            _viewModel?.CanMoveSelectionToFolder(node.Item) == true)
        {
            args.DragEffects = DragDropEffects.Move;
            await _viewModel.MoveSelectionToFolderAsync(node.Item);
        }
        else
        {
            args.DragEffects = DragDropEffects.None;
        }
    }

    private void MessageListKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter)
        {
            ShowPhoneMessage();
        }
    }

    private void MainWindowKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Handled || _viewModel is null)
        {
            return;
        }

        if (args.Key == Key.Escape && _viewModel.IsGlobalSearchOpen)
        {
            Execute(_viewModel.CloseGlobalSearchCommand);
            args.Handled = true;
            return;
        }

        var hasMailBackStage = _layoutMode != ResponsiveLayoutMode.Wide &&
                               (_showFolders || (_layoutMode == ResponsiveLayoutMode.Phone && _showPhoneMessage));
        var focusedElement = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        var action = ShellActionFor(
            args.Key,
            _viewModel.IsMailModule,
            _viewModel.IsSettingsOpen,
            hasMailBackStage,
            args.Source is TextBox || focusedElement is TextBox);
        switch (action)
        {
            case ShellKeyAction.CloseSettings:
                Execute(_viewModel.CloseSettingsCommand);
                break;
            case ShellKeyAction.BackToMessageList:
                _showFolders = false;
                _showPhoneMessage = false;
                UpdateMailPanes();
                break;
            case ShellKeyAction.Archive:
                if (_viewModel.ArchiveCommand.CanExecute(null))
                {
                    _viewModel.ArchiveCommand.Execute(null);
                }
                else
                {
                    return;
                }
                break;
            case ShellKeyAction.Delete:
                if (_viewModel.DeleteCommand.CanExecute(null))
                {
                    _viewModel.DeleteCommand.Execute(null);
                }
                else
                {
                    return;
                }
                break;
            default:
                return;
        }
        args.Handled = true;
    }

    private void GlobalSearchKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter && _viewModel?.SearchCommand.CanExecute(null) == true)
        {
            _viewModel.SearchCommand.Execute(null);
            args.Handled = true;
        }
    }

    private async void CopyErrorClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (_viewModel?.Error is { Length: > 0 } error && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetValueAsync(DataFormat.Text, error);
        }
    }

    private void CloseErrorClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        _viewModel?.DismissError();

    private void MessageRowDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (sender is not Border { DataContext: MailMessage message } || _viewModel is null)
        {
            return;
        }

        var identity = BetterMail.Core.ConversationThread.ThreadIdentity(message);
        var previewViewModel = new ConversationThreadViewModel();
        previewViewModel.Reconcile(
            _viewModel.Messages.Where(candidate =>
                BetterMail.Core.ConversationThread.ThreadIdentity(candidate) == identity),
            message);
        new Window
        {
            Title = message.Subject,
            Icon = Icon,
            Width = 920,
            Height = 720,
            MinWidth = 420,
            MinHeight = 360,
            Content = new ConversationThreadView { DataContext = previewViewModel }
        }.Show(this);
        args.Handled = true;
    }

    private void MessageReplyClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        Execute(_viewModel?.ReplyCommand);
    private void MessageReplyAllClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        Execute(_viewModel?.ReplyAllCommand);
    private void MessageForwardClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        Execute(_viewModel?.ForwardCommand);
    private void MessageArchiveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        Execute(_viewModel?.ArchiveCommand);
    private void MessageDeleteClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        Execute(_viewModel?.DeleteCommand);
    private void MessageJunkClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        Execute(_viewModel?.JunkCommand);
    private void MessageFlagClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        Execute(_viewModel?.ToggleFlagCommand);
    private void MessageReadClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        Execute(_viewModel?.ToggleReadCommand);

    private void QuickReadClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        SelectAndExecute(sender, _viewModel?.ToggleReadCommand);
    private void QuickFlagClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        SelectAndExecute(sender, _viewModel?.ToggleFlagCommand);
    private void QuickArchiveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        SelectAndExecute(sender, _viewModel?.ArchiveCommand);
    private void QuickDeleteClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        SelectAndExecute(sender, _viewModel?.DeleteCommand);

    private void SelectAndExecute(object? sender, ICommand? command)
    {
        if (_viewModel is null || sender is not Control { DataContext: MailMessage message })
        {
            return;
        }
        var selectedItems = MessageList.SelectedItems;
        if (selectedItems is null)
        {
            return;
        }
        if (!selectedItems.Contains(message))
        {
            selectedItems.Clear();
            selectedItems.Add(message);
        }
        _viewModel.SetSelectedMessages(selectedItems.OfType<MailMessage>());
        Execute(command);
    }

    private static void Execute(ICommand? command)
    {
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
    }

    private void ShowPhoneMessage()
    {
        if (_layoutMode != ResponsiveLayoutMode.Phone || _viewModel?.SelectedMessage is null)
        {
            return;
        }

        _showPhoneMessage = true;
        UpdateMailPanes();
    }

    private void PhoneBackClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        _showPhoneMessage = false;
        UpdateMailPanes();
    }

    private void BindViewModel(MainWindowViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.ComposeRequested -= OpenCompose;
            _viewModel.SharedMailboxRequested -= OpenSharedMailbox;
            _viewModel.SearchFocusRequested -= FocusMailSearch;
            _viewModel.HeadersRequested -= OpenHeaders;
            _viewModel.PropertyChanged -= ViewModelPropertyChanged;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.ComposeRequested += OpenCompose;
            _viewModel.SharedMailboxRequested += OpenSharedMailbox;
            _viewModel.SearchFocusRequested += FocusMailSearch;
            _viewModel.HeadersRequested += OpenHeaders;
            _viewModel.PropertyChanged += ViewModelPropertyChanged;
        }
        UpdateMailPanes();
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainWindowViewModel.IsSyncing) && _viewModel is not null)
        {
            if (_viewModel.IsSyncing)
            {
                _messageScrollOffsetBeforeSync = MessageList
                    .GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.Offset;
            }
            else if (_messageScrollOffsetBeforeSync is { } offset)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var scroll = MessageList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                    if (scroll is not null)
                    {
                        scroll.Offset = offset;
                    }
                    _messageScrollOffsetBeforeSync = null;
                }, DispatcherPriority.Loaded);
            }
        }
        if (args.PropertyName is nameof(MainWindowViewModel.ShowMailSurface)
            or nameof(MainWindowViewModel.IsSettingsOpen)
            or nameof(MainWindowViewModel.ActiveModule))
        {
            UpdateMailPanes();
        }
    }

    private void FocusMailSearch()
    {
        MailSearch.Focus();
        MailSearch.SelectAll();
    }

    private void OpenCompose(ComposeRequest request)
    {
        if (_viewModel is null)
        {
            return;
        }

        var window = new ComposeWindow(
            _viewModel.Accounts,
            _viewModel.Mailboxes,
            request,
            _viewModel.SendDraftAsync,
            _viewModel.SaveLocalDraftAsync,
            _viewModel.DeleteLocalDraftAsync,
            _viewModel.SignatureForSender,
            _viewModel.FilesProvider,
            _viewModel.SearchRecipientSuggestionsAsync);
        _ = window.ShowDialog<bool>(this);
    }

    private void OpenSharedMailbox(MailAccount account)
    {
        if (_viewModel is null)
        {
            return;
        }

        var window = new SharedMailboxWindow(account, _viewModel.AddSharedMailboxAsync);
        _ = window.ShowDialog<bool>(this);
    }

    private void OpenHeaders(MailHeadersDocument document) =>
        new MailHeadersWindow(document).Show(this);

    private async void SaveAttachmentClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: MailAttachment attachment } || attachment.ContentBytes is null)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save attachment",
            SuggestedFileName = attachment.Name
        });
        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(attachment.ContentBytes);
    }

    private void OpenWorkspaceLinkClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: Uri uri })
        {
            return;
        }

        try
        {
            _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // The module remains usable when Windows has no handler for the link.
        }
    }
}

internal enum ResponsiveLayoutMode
{
    Phone,
    Compact,
    Wide
}

internal enum ShellKeyAction
{
    None,
    CloseSettings,
    BackToMessageList,
    Archive,
    Delete
}

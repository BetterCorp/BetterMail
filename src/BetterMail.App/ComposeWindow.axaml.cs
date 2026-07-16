using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using BetterMail.Core;

namespace BetterMail.App;

public sealed partial class ComposeWindow : Window
{
    private bool _closeAfterSave;
    private bool _dialogResult;
    private IFilesProvider? _filesProvider;

    public ComposeWindow()
    {
        InitializeComponent();
        Closing += SaveBeforeClosing;
    }

    public ComposeWindow(
        IReadOnlyList<MailAccount> accounts,
        IReadOnlyList<Mailbox> mailboxes,
        ComposeRequest request,
        Func<ComposeSender, string, DraftMessage, Task> send,
        Func<LocalDraft, Task> saveDraft,
        Func<string, Task> deleteDraft,
        Func<ComposeSender, ComposeIntent, SignatureContent?> signatureForSender,
        IFilesProvider? filesProvider = null,
        Func<string, CancellationToken, Task<IReadOnlyList<RecipientSuggestion>>>? searchRecipients = null) : this()
    {
        _filesProvider = filesProvider;
        var viewModel = new ComposeWindowViewModel(
            accounts, mailboxes, request, send, saveDraft, deleteDraft,
            signatureForSender: signatureForSender,
            searchRecipients: searchRecipients);
        viewModel.Sent += (_, _) =>
        {
            _dialogResult = true;
            Close();
        };
        DataContext = viewModel;
    }

    private void CancelClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void ComposeWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void RecipientQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ComposeRecipientField field } ||
            e.Key is not (Key.Enter or Key.Tab) && e.Key != Key.OemSemicolon)
        {
            return;
        }
        try
        {
            field.CommitQuery();
            e.Handled = true;
        }
        catch (FormatException)
        {
            // Keep the invalid text in place; Send reports the actionable validation error.
        }
    }

    private async void SaveBeforeClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeAfterSave || DataContext is not ComposeWindowViewModel viewModel)
        {
            return;
        }

        e.Cancel = true;
        await CaptureEditorBodyAsync();
        await viewModel.FlushDraftAsync();
        _closeAfterSave = true;
        Close(_dialogResult);
    }

    private async void SendClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await CaptureEditorBodyAsync();
        if (DataContext is ComposeWindowViewModel viewModel && viewModel.SendCommand.CanExecute(null))
        {
            viewModel.SendCommand.Execute(null);
        }
    }

    private Task CaptureEditorBodyAsync() => Composer.CaptureAsync();

    private async void AttachFileClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not ComposeWindowViewModel viewModel)
        {
            return;
        }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Attach files", AllowMultiple = true });
        foreach (var file in files)
        {
            await using var stream = await file.OpenReadAsync();
            if (stream.CanSeek && !viewModel.ValidateAttachmentSize(file.Name, stream.Length))
            {
                continue;
            }
            await using var content = new LimitedMemoryStream(DraftAttachment.MaximumSizeBytes);
            try
            {
                await stream.CopyToAsync(content);
                viewModel.AddAttachment(new DraftAttachment(file.Name, "application/octet-stream", content.ToArray()));
            }
            catch (InvalidOperationException)
            {
                viewModel.ValidateAttachmentSize(file.Name, DraftAttachment.MaximumSizeBytes + 1);
            }
        }
    }

    private async void AttachDriveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not ComposeWindowViewModel viewModel)
        {
            return;
        }
        if (_filesProvider is null)
        {
            viewModel.ReportError("OneDrive is unavailable.");
            return;
        }

        var accounts = viewModel.Senders.Select(static sender => sender.Account)
            .DistinctBy(static account => account.AccountId).ToArray();
        var selection = await new DrivePickerWindow(_filesProvider, accounts)
            .ShowDialog<DriveProviderSelection?>(this);
        if (selection is null || !viewModel.ValidateAttachmentSize(selection.Item.Name, selection.Item.Size))
        {
            return;
        }

        try
        {
            await using var content = new LimitedMemoryStream(DraftAttachment.MaximumSizeBytes);
            await _filesProvider.DownloadFileAsync(selection.Account, selection.Item, content);
            viewModel.AddAttachment(new DraftAttachment(
                selection.Item.Name,
                selection.Item.ContentType ?? "application/octet-stream",
                content.ToArray()));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            viewModel.ReportError($"'{selection.Item.Name}' could not be attached: {exception.Message}");
        }
    }
}

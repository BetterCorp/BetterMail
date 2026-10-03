using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using BetterMail.Core;

namespace BetterMail.App;

public sealed partial class ComposeWindow : Window
{
    private sealed record RecipientDrag(ComposeWindowViewModel Owner, ComposeRecipientField Source, ComposeRecipientToken Token);
    private static readonly DataFormat<RecipientDrag> RecipientDragFormat = DataFormat.CreateInProcessFormat<RecipientDrag>("BetterMail.Recipient");
    private PointerPressedEventArgs? _recipientDragStart;
    private Avalonia.Point _recipientDragOrigin;
    private RecipientDrag? _recipientDrag;
    private bool _closeAfterSave;
    private IFilesProvider? _filesProvider;

    public ComposeWindow()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, FilesDragOver);
        AddHandler(DragDrop.DropEvent, FilesDropped);
        Composer.AttachmentDropped += async attachment =>
        {
            if (DataContext is ComposeWindowViewModel viewModel)
                await viewModel.AttachDroppedAsync(attachment, async file =>
                {
                    using var content = new MemoryStream(file.ContentBytes, writable: false);
                    await AttachLargeFileAsync(viewModel, file.Name, content, content.Length);
                });
        };
        Closing += SaveBeforeClosing;
        Opened += (_, _) => FocusToRecipient();
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
        viewModel.Sent += (_, _) => Close();
        viewModel.Deleted += (_, _) =>
        {
            _closeAfterSave = true;
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

    private void FocusToRecipient()
    {
        if (DataContext is not ComposeWindowViewModel viewModel)
        {
            return;
        }
        this.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(textBox => ReferenceEquals(textBox.DataContext, viewModel.ToField))
            ?.Focus();
    }

    private static void RecipientQueryGotFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: ComposeRecipientField field })
        {
            field.RefreshSearch();
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
            if (e.Key is Key.Enter or Key.Tab && field.CommitFirstSuggestion())
            {
                e.Handled = e.Key != Key.Tab;
                return;
            }
            field.CommitQuery();
            e.Handled = e.Key != Key.Tab;
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
        if (viewModel.IsUploadingAttachment)
        {
            viewModel.ReportError("Please wait for the attachment upload to finish before closing this draft.");
            return;
        }
        await CaptureEditorBodyAsync();
        await viewModel.FlushDraftAsync();
        _closeAfterSave = true;
        Close();
    }

    private async void SendClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await CaptureEditorBodyAsync();
        if (DataContext is ComposeWindowViewModel viewModel && viewModel.SendCommand.CanExecute(null))
        {
            viewModel.SendCommand.Execute(null);
        }
    }

    private async void CopyErrorClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is ComposeWindowViewModel { Error.Length: > 0 } viewModel &&
            TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetValueAsync(DataFormat.Text, viewModel.Error);
        }
    }

    private void CloseErrorClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is ComposeWindowViewModel viewModel)
        {
            viewModel.DismissError();
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
        await AttachFilesAsync(viewModel, files);
    }

    private static void RecipientFieldPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Avalonia.Visual source && source.GetSelfAndVisualAncestors().Any(static visual => visual is TextBox or Button)) return;
        if (sender is Border border)
            border.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus();
    }

    private void RecipientTokenPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: ComposeRecipientToken token } chip ||
            DataContext is not ComposeWindowViewModel { CanChangeAttachments: true } viewModel ||
            !e.GetCurrentPoint(chip).Properties.IsLeftButtonPressed ||
            e.Source is Avalonia.Visual visual && visual.GetSelfAndVisualAncestors().Any(ancestor => ancestor is Button)) return;
        var source = chip.GetVisualAncestors().OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("recipientField"))?.DataContext as ComposeRecipientField;
        if (source is null) return;
        _recipientDragStart = e;
        _recipientDragOrigin = e.GetPosition(this);
        _recipientDrag = new(viewModel, source, token);
        e.Pointer.Capture(chip);
        e.Handled = true;
    }

    private async void RecipientTokenMoved(object? sender, PointerEventArgs e)
    {
        if (_recipientDragStart is not { } pressed || _recipientDrag is not { } drag ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _recipientDragOrigin.X) < 6 && Math.Abs(position.Y - _recipientDragOrigin.Y) < 6) return;
        _recipientDragStart = null;
        _recipientDrag = null;
        e.Pointer.Capture(null);
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(RecipientDragFormat, drag));
        e.Handled = true;
        try { await DragDrop.DoDragDropAsync(pressed, data, DragDropEffects.Move); }
        finally
        {
            foreach (var border in this.GetVisualDescendants().OfType<Border>())
                border.Classes.Remove("recipientDropTarget");
        }
    }

    private void RecipientTokenReleased(object? sender, PointerReleasedEventArgs e)
    {
        _recipientDragStart = null;
        _recipientDrag = null;
        e.Pointer.Capture(null);
    }

    private void RecipientTokenCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _recipientDragStart = null;
        _recipientDrag = null;
    }

    private bool CanDropRecipient(Border border, RecipientDrag drag) =>
        ReferenceEquals(DataContext, drag.Owner) && border.DataContext is ComposeRecipientField target &&
        drag.Owner.CanMoveRecipient(drag.Source, target, drag.Token);

    private void RecipientDragOver(object? sender, DragEventArgs e)
    {
        if (sender is not Border border || e.DataTransfer.TryGetValue(RecipientDragFormat) is not { } drag) return;
        var allowed = CanDropRecipient(border, drag);
        border.Classes.Set("recipientDropTarget", allowed);
        e.DragEffects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private static void RecipientDragLeave(object? sender, DragEventArgs e)
    {
        if (sender is Border border) border.Classes.Remove("recipientDropTarget");
    }

    private void RecipientDropped(object? sender, DragEventArgs e)
    {
        if (sender is not Border border || e.DataTransfer.TryGetValue(RecipientDragFormat) is not { } drag) return;
        border.Classes.Remove("recipientDropTarget");
        e.DragEffects = CanDropRecipient(border, drag) &&
            drag.Owner.MoveRecipient(drag.Source, (ComposeRecipientField)border.DataContext!, drag.Token)
            ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
        if (e.DragEffects == DragDropEffects.Move)
            border.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus();
    }

    private void FilesDragOver(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Formats.Contains(DataFormat.File))
        {
            return;
        }
        e.DragEffects = DataContext is ComposeWindowViewModel { CanChangeAttachments: true }
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void FilesDropped(object? sender, DragEventArgs e)
    {
        if (DataContext is not ComposeWindowViewModel viewModel ||
            e.DataTransfer.TryGetFiles() is not { } files)
        {
            return;
        }
        e.Handled = true;
        await AttachFilesAsync(viewModel, files.OfType<IStorageFile>());
    }

    private async Task AttachFilesAsync(ComposeWindowViewModel viewModel, IEnumerable<IStorageFile> files)
    {
        if (!viewModel.TryBeginFileAttachmentUpload(files.Select(file => file.Name))) return;
        try
        {
            foreach (var file in files)
            {
                try
                {
                    await using var stream = await file.OpenReadAsync();
                    if (stream.CanSeek && LargeAttachmentPolicy.UseDrive(stream.Length, viewModel.Attachments))
                    {
                        await AttachLargeFileAsync(viewModel, file.Name, stream, stream.Length);
                        continue;
                    }
                    if (stream.CanSeek && !viewModel.ValidateAttachmentSize(file.Name, stream.Length))
                    {
                        continue;
                    }
                    await using var content = new LimitedMemoryStream(DraftAttachment.MaximumSizeBytes);
                    await stream.CopyToAsync(content);
                    if (LargeAttachmentPolicy.UseDrive(content.Length, viewModel.Attachments))
                    {
                        content.Position = 0;
                        await AttachLargeFileAsync(viewModel, file.Name, content, content.Length);
                    }
                    else viewModel.AddAttachment(new DraftAttachment(file.Name, "application/octet-stream", content.ToArray()));
                }
                catch (Exception exception)
                {
                    viewModel.ReportError($"'{file.Name}' could not be attached: {exception.Message}");
                }
            }
        }
        finally { viewModel.IsUploadingAttachment = false; }
    }

    private async Task AttachLargeFileAsync(ComposeWindowViewModel viewModel, string name, Stream content, long length)
    {
        var sender = viewModel.SelectedSender;
        if (_filesProvider is null || sender?.Account.Capabilities.HasFlag(ProviderCapabilities.Files) != true)
        {
            viewModel.ReportError("Attachments above 20 MiB total need a sender with a connected Drive account. Select a OneDrive account or use a smaller file.");
            return;
        }
        var folder = await LargeAttachmentPolicy.AttachmentsFolderAsync(_filesProvider, sender.Account);
        var item = await _filesProvider.UploadFileAsync(sender.Account, folder, AttachmentDriveSaveViewModel.NormalizeFileName(name), content, length, "application/octet-stream");
        try
        {
            var link = await _filesProvider.CreateReadOnlyLinkAsync(sender.Account, item, DateTimeOffset.UtcNow.AddYears(1), "anonymous", []);
            if (viewModel.SelectedSender != sender) throw new InvalidOperationException("The sender changed. The uploaded file remains in the original account's Attachments folder.");
            await CaptureEditorBodyAsync();
            viewModel.Body += LargeAttachmentPolicy.LinkHtml(item.Name, link);
        }
        catch (Exception exception)
        {
            viewModel.ReportError($"'{item.Name}' was uploaded to Attachments, but its link could not be added: {exception.Message}");
        }
    }

    private void AttachDriveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not ComposeWindowViewModel viewModel)
        {
            return;
        }
        var provider = _filesProvider;
        if (provider is null)
        {
            viewModel.ReportError("Drive is unavailable.");
            return;
        }

        var accounts = viewModel.Senders.Select(static sender => sender.Account)
            .DistinctBy(static account => account.AccountId).ToArray();
        var picker = new DrivePickerWindow(provider, accounts);
        picker.Completed += selection => _ = AttachDriveSelectionAsync(viewModel, provider, selection);
        IndependentWindow.Show(picker);
    }

    private async Task AttachDriveSelectionAsync(
        ComposeWindowViewModel viewModel,
        IFilesProvider provider,
        DriveProviderSelection? selection)
    {
        if (!IsVisible || selection is null)
        {
            return;
        }

        if (!viewModel.TryBeginFileAttachmentUpload([selection.Item.Name])) return;
        async Task ShareAsync()
        {
            var link = await provider.CreateReadOnlyLinkAsync(selection.Account, selection.Item, DateTimeOffset.UtcNow.AddYears(1), "anonymous", []);
            await CaptureEditorBodyAsync();
            viewModel.Body += LargeAttachmentPolicy.LinkHtml(selection.Item.Name, link);
        }
        try
        {
            if (LargeAttachmentPolicy.UseDrive(selection.Item.Size, viewModel.Attachments))
            {
                await ShareAsync();
                return;
            }
            await using var content = new LimitedMemoryStream(DraftAttachment.MaximumSizeBytes);
            await provider.DownloadFileAsync(selection.Account, selection.Item, content);
            await viewModel.AttachDownloadedFileAsync(selection.Item.Name, selection.Item.ContentType, content, ShareAsync);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            viewModel.ReportError($"'{selection.Item.Name}' could not be attached: {exception.Message}");
        }
        finally { viewModel.IsUploadingAttachment = false; }
    }
}

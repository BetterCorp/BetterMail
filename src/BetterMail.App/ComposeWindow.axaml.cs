using System.ComponentModel;
using System.Text.Json;
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
    private bool _editorReady;
    private bool _updatingFromEditor;

    public ComposeWindow()
    {
        InitializeComponent();
        Closing += SaveBeforeClosing;
        Opened += InitializeEditor;
    }

    public ComposeWindow(
        IReadOnlyList<MailAccount> accounts,
        IReadOnlyList<Mailbox> mailboxes,
        ComposeRequest request,
        Func<ComposeSender, string, DraftMessage, Task> send,
        Func<LocalDraft, Task> saveDraft,
        Func<string, Task> deleteDraft,
        Func<ComposeSender, string> signatureForSender,
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
        viewModel.PropertyChanged += ViewModelPropertyChanged;
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

    private void InitializeEditor(object? sender, EventArgs e)
    {
        if (DataContext is not ComposeWindowViewModel viewModel)
        {
            return;
        }

        var document = $$"""
            <!doctype html><html><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'; script-src 'unsafe-inline'">
            <meta name="color-scheme" content="light dark">
            <style>
              html,body { height:100%; margin:0; background:Canvas; color:CanvasText; }
              body { box-sizing:border-box; padding:14px 16px 40px; font:15px/1.5 "Segoe UI",system-ui,sans-serif; overflow-wrap:anywhere; }
              body:empty:before { content:'Write something useful...'; color:GrayText; pointer-events:none; }
              blockquote { margin:12px 0; padding-left:12px; border-left:2px solid #999; color:GrayText; }
              img,table { max-width:100%; } a { color:#0f6cbd; }
            </style></head><body id="editor" contenteditable="true">{{viewModel.Body}}</body>
            <script>editor.addEventListener('input',()=>invokeCSharpAction(editor.innerHTML)); editor.focus();</script>
            </html>
            """;
        Composer.NavigateToString(document, new Uri("about:blank"));
    }

    private void ComposerNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e) =>
        _editorReady = e.IsSuccess;

    private void ComposerWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        if (DataContext is not ComposeWindowViewModel viewModel)
        {
            return;
        }
        _updatingFromEditor = true;
        viewModel.Body = e.Body ?? "";
        _updatingFromEditor = false;
    }

    private async void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ComposeWindowViewModel.Body) || _updatingFromEditor || !_editorReady ||
            sender is not ComposeWindowViewModel viewModel)
        {
            return;
        }
        await Composer.InvokeScript($"document.getElementById('editor').innerHTML={JsonSerializer.Serialize(viewModel.Body)}");
    }

    private async void FormatClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!_editorReady || sender is not Button { CommandParameter: string command } button)
        {
            return;
        }
        var script = command switch
        {
            "createLink" => "const u=prompt('Link address'); if(u) document.execCommand('createLink',false,u)",
            "formatBlock" => $"document.execCommand('formatBlock',false,{JsonSerializer.Serialize(button.Tag?.ToString() ?? "blockquote")})",
            _ => $"document.execCommand({JsonSerializer.Serialize(command)},false,null)"
        };
        await Composer.InvokeScript(script);
        await Composer.InvokeScript("document.getElementById('editor').focus()");
    }

    private async void SendClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await CaptureEditorBodyAsync();
        if (DataContext is ComposeWindowViewModel viewModel && viewModel.SendCommand.CanExecute(null))
        {
            viewModel.SendCommand.Execute(null);
        }
    }

    private async Task CaptureEditorBodyAsync()
    {
        if (_editorReady)
        {
            await Composer.InvokeScript("invokeCSharpAction(document.getElementById('editor').innerHTML)");
        }
    }

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

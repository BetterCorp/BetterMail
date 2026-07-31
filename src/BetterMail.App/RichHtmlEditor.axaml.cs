using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace BetterMail.App;

public sealed partial class RichHtmlEditor : UserControl
{
    private const long MaximumImageBytes = 2 * 1024 * 1024;
    private bool _ready;
    private bool _updatingFromEditor;
    private bool _initialized;

    public static readonly StyledProperty<string> HtmlProperty = AvaloniaProperty.Register<RichHtmlEditor, string>(
        nameof(Html), "", defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsReadOnlyProperty = AvaloniaProperty.Register<RichHtmlEditor, bool>(
        nameof(IsReadOnly));

    public static readonly StyledProperty<string> PlaceholderProperty = AvaloniaProperty.Register<RichHtmlEditor, string>(
        nameof(Placeholder), "Write something useful…");

    public RichHtmlEditor()
    {
        InitializeComponent();
        AttachedToVisualTree += InitializeEditor;
    }

    public string Html
    {
        get => GetValue(HtmlProperty);
        set => SetValue(HtmlProperty, value);
    }

    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public string Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    protected override async void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (!_ready || _updatingFromEditor)
        {
            return;
        }
        if (change.Property == HtmlProperty)
        {
            await Editor.InvokeScript($"editor.innerHTML={JsonSerializer.Serialize(Html)}");
        }
        else if (change.Property == IsReadOnlyProperty)
        {
            await Editor.InvokeScript($"editor.contentEditable='{(!IsReadOnly).ToString().ToLowerInvariant()}'");
        }
    }

    public async Task CaptureAsync()
    {
        if (_ready)
        {
            await Editor.InvokeScript("invokeCSharpAction(editor.innerHTML)");
        }
    }

    private void InitializeEditor(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;
        var document = $$"""
            <!doctype html><html><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'; script-src 'unsafe-inline'">
            <meta name="color-scheme" content="light dark">
            <style>
              html,body { height:100%; margin:0; background:Canvas; color:CanvasText; }
              body { box-sizing:border-box; padding:14px 16px 40px; font:15px/1.5 "Segoe UI",system-ui,sans-serif; overflow-wrap:anywhere; }
              body:empty:before { content:attr(data-placeholder); color:GrayText; pointer-events:none; }
              blockquote { margin:12px 0; padding-left:12px; border-left:2px solid #999; color:GrayText; }
              img,table { max-width:100%; } img { height:auto; } a { color:#0f6cbd; }
              td,th { border:1px solid #aaa; padding:4px 6px; }
            </style></head><body id="editor"
              data-placeholder="{{System.Net.WebUtility.HtmlEncode(Placeholder)}}"
              contenteditable="{{(!IsReadOnly).ToString().ToLowerInvariant()}}"></body>
            <script>
              editor.innerHTML={{JsonSerializer.Serialize(Html)}};
              editor.addEventListener('input',()=>invokeCSharpAction(editor.innerHTML));
              if(editor.contentEditable==='true') editor.focus();
            </script></html>
            """;
        Editor.NavigateToString(document, new Uri("about:blank"));
    }

    private void EditorNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e) => _ready = e.IsSuccess;

    private void EditorWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        _updatingFromEditor = true;
        Html = e.Body ?? "";
        _updatingFromEditor = false;
    }

    private async void FormatClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!_ready || IsReadOnly || sender is not Button { CommandParameter: string command } button)
        {
            return;
        }
        var script = command switch
        {
            "createLink" => "const u=prompt('Link address'); if(u) document.execCommand('createLink',false,u)",
            "formatBlock" => $"document.execCommand('formatBlock',false,{JsonSerializer.Serialize(button.Tag?.ToString() ?? "blockquote")})",
            "fontName" => "const f=prompt('Font name','Segoe UI'); if(f) document.execCommand('fontName',false,f)",
            "fontSize" => "const s=prompt('Font size from 1 to 7','3'); if(s) document.execCommand('fontSize',false,s)",
            "foreColor" => "const c=prompt('Text colour (name or hex)','#157efb'); if(c) document.execCommand('foreColor',false,c)",
            "hiliteColor" => "const c=prompt('Highlight colour (name or hex)','#fff2cc'); if(c) document.execCommand('hiliteColor',false,c)",
            "insertTable" => "const r=Math.min(10,Math.max(1,parseInt(prompt('Rows','2'))||0));const c=Math.min(8,Math.max(1,parseInt(prompt('Columns','2'))||0));if(r&&c){let h='<table><tbody>';for(let y=0;y<r;y++){h+='<tr>';for(let x=0;x<c;x++)h+='<td>&nbsp;</td>';h+='</tr>';}document.execCommand('insertHTML',false,h+'</tbody></table><p><br></p>')}",
            _ => $"document.execCommand({JsonSerializer.Serialize(command)},false,null)"
        };
        await Editor.InvokeScript(script);
        await Editor.InvokeScript("editor.focus()");
    }

    private async void InsertImageClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!_ready || IsReadOnly || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Insert image",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll]
        });
        var file = files.FirstOrDefault();
        if (file is null)
        {
            return;
        }
        await using var stream = await file.OpenReadAsync();
        if (stream.CanSeek && stream.Length > MaximumImageBytes)
        {
            ShowError("Signature images must be 2 MB or smaller.");
            return;
        }
        await using var content = new LimitedMemoryStream(MaximumImageBytes);
        try
        {
            await stream.CopyToAsync(content);
        }
        catch (InvalidOperationException)
        {
            ShowError("Signature images must be 2 MB or smaller.");
            return;
        }
        var extension = Path.GetExtension(file.Name).ToLowerInvariant();
        var contentType = extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/png"
        };
        EditorError.IsVisible = false;
        var source = $"data:{contentType};base64,{Convert.ToBase64String(content.ToArray())}";
        await Editor.InvokeScript($"document.execCommand('insertImage',false,{JsonSerializer.Serialize(source)});editor.focus()");
    }

    private void ShowError(string message)
    {
        EditorError.Text = message;
        EditorError.IsVisible = true;
    }
}

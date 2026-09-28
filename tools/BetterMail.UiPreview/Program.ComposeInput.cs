using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using BetterMail.App;
using BetterMail.Core;

internal static partial class Program
{
    // Offline regression: real X11 keyboard/mouse events, real compose bindings, no accounts or network.
    private static async Task CheckComposeInputAsync()
    {
        var account = new MailAccount("microsoft365", "fictional", "tenant", "author@example.test", "Author", ProviderCapabilities.Mail);
        LocalDraft? saved = null;
        var mainWeb = new NativeWebView();
        var main = new Window { Content = mainWeb, Width = 900, Height = 750 };
        main.Show();
        mainWeb.NavigateToString("<p>Fictional reading pane</p>");
        var window = new ComposeWindow([account], [new(account.AccountId, account.EmailAddress, "Author")],
            new ComposeRequest(Body: "<p>Draft</p>", IsHtml: true),
            (_, _, _) => throw new InvalidOperationException("This test must never send mail."),
            draft => { saved = draft; return Task.CompletedTask; }, _ => Task.CompletedTask,
            (_, _) => null)
        { Position = new(0, 0), WindowStartupLocation = WindowStartupLocation.Manual, WindowDecorations = WindowDecorations.None };
        var editor = window.FindControl<RichHtmlEditor>("Composer")!;
        var web = editor.FindControl<NativeWebView>("Editor")!;
        var navigation = new TaskCompletionSource();
        web.NavigationCompleted += (_, args) =>
        {
            if (args.IsSuccess) navigation.TrySetResult();
            else navigation.TrySetException(new InvalidOperationException("Compose editor failed to load."));
        };
        try
        {
            window.Show();
            window.Activate();
            await navigation.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(300);
            // Start in To, as the real compose window does, then click into the body.
            var recipient = window.GetVisualDescendants().OfType<TextBox>()
                .First(box => box.DataContext is ComposeRecipientField { Label: "To" });
            recipient.Focus();
            await Click(web);
            await Input("Control_L", "a");
            foreach (var key in new[] { "h", "e", "l", "l", "o" }) await Input("none", key);
            await Input("Shift_L", "1");
            await Input("none", "BackSpace");
            await Input("Shift_L", "1");
            await ExpectText("hello!");
            await Input("none", "Return");
            await Input("Shift_L", "a");
            await ExpectText("hello!\nA");
            var beforeUndo = await web.InvokeScript("editor.innerHTML");
            await Click(editor.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.CommandParameter, "undo")));
            if (await web.InvokeScript("editor.innerHTML") == beforeUndo)
                throw new InvalidOperationException("Undo did not change the typed content.");
            await Click(editor.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.CommandParameter, "redo")));
            await ExpectText("hello!\nA");
            // Leave and re-enter the editor without losing the caret or directing text to To.
            await Click(recipient);
            await Input("none", "r");
            // Keyboard navigation uses Avalonia focus rather than a native GTK mouse event.
            web.Focus();
            await Task.Delay(100);
            await Input("Control_L", "End");
            await Input("none", "b");
            await ExpectText("hello!\nAb");
            if (recipient.Text != "r") throw new InvalidOperationException("Editor input leaked into the recipient field.");
            await editor.CaptureAsync();
            await ((ComposeWindowViewModel)window.DataContext!).FlushDraftAsync();
            if (saved is null || !saved.Body.Contains("hello!") || !saved.Body.Contains('b'))
                throw new InvalidOperationException("Typed HTML was not captured in the saved draft.");
            editor.IsReadOnly = true;
            await Task.Delay(100);
            await Input("none", "x");
            await ExpectText("hello!\nAb");
            Console.WriteLine("Linux compose input passed: typing, Shift, Backspace, Enter, undo, focus switching and draft capture.");
        }
        finally { window.Close(); main.Close(); }

        async Task ExpectText(string expected)
        {
            var expectedJson = System.Text.Json.JsonSerializer.Serialize(expected);
            var result = await web.InvokeScript($"editor.innerText.replace(/\\n+/g,'\\n') === {expectedJson}");
            if (result != "true")
                throw new InvalidOperationException($"Expected editor text {expectedJson}; received {await web.InvokeScript("editor.innerText")}.");
        }

        async Task Click(Control control)
        {
            var point = control.PointToScreen(new Point(35, Math.Min(20, control.Bounds.Height / 2)));
            await Input("none", "click", point.X.ToString(), point.Y.ToString());
        }

        static async Task Input(params string[] args)
        {
            var start = new ProcessStartInfo("python3");
            start.ArgumentList.Add("tools/BetterMail.UiPreview/input.py");
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException("Input injection failed.");
            await Task.Delay(100);
        }
    }
}

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using BetterMail.App;
using BetterMail.Core;

internal static partial class Program
{
    // Exercise native drag/drop through the real recipient templates with fictional offline data.
    private static async Task CheckRecipientDraggingAsync()
    {
        LocalDraft? saved = null;
        var account = new MailAccount("microsoft365", "fictional", "tenant", "author@example.test", "Author", ProviderCapabilities.Mail);
        var window = new ComposeWindow([account], [new(account.AccountId, account.EmailAddress, "Author")],
            new ComposeRequest(To: "already@example.test", Cc: "\"Doe, Jane\" <jane@example.test>", Bcc: "hidden@example.test"),
            (_, _, _) => throw new InvalidOperationException("This test must never send mail."),
            draft => { saved = draft; return Task.CompletedTask; }, _ => Task.CompletedTask, (_, _) => null)
        { Position = new(0, 0), WindowStartupLocation = WindowStartupLocation.Manual, WindowDecorations = WindowDecorations.None };
        var vm = (ComposeWindowViewModel)window.DataContext!;
        var token = vm.CcField.Tokens.Single();
        try
        {
            window.Show();
            window.Activate();
            await Task.Delay(600);
            var subject = window.GetVisualDescendants().OfType<TextBox>()
                .Single(box => Avalonia.Automation.AutomationProperties.GetName(box) == "Subject");
            foreach (var field in vm.RecipientFields)
            {
                // A chip click must still focus its field when no drag follows.
                await Click(subject);
                await Click(Chip(field.Tokens[0]));
                await Input("r");
                if (field.Query != "r" || subject.Text?.Length > 0)
                    throw new InvalidOperationException($"Clicking a {field.Label} chip did not direct typing to its recipient input.");
                field.Query = "";
            }
            foreach (var field in vm.RecipientFields) field.Query = $"pending-{field.Label.ToLowerInvariant()}@example.test";
            foreach (var (source, target) in new[]
            {
                (vm.CcField, vm.ToField), (vm.ToField, vm.BccField), (vm.BccField, vm.CcField),
                (vm.CcField, vm.BccField), (vm.BccField, vm.ToField), (vm.ToField, vm.CcField)
            })
            {
                await Drag(Chip(token), Field(target));
                if (source.Tokens.Contains(token) || !target.Tokens.Any(existing => ReferenceEquals(existing, token)))
                    throw new InvalidOperationException($"Recipient drag from {source.Label} to {target.Label} failed.");
                foreach (var field in vm.RecipientFields)
                    if (field.Query != $"pending-{field.Label.ToLowerInvariant()}@example.test")
                        throw new InvalidOperationException($"Dragging replaced pending {field.Label} text.");
                await vm.FlushDraftAsync();
                var fields = new[] { saved!.To, saved.Cc, saved.Bcc };
                if (fields.Count(value => value.Contains("jane@example.test")) != 1 ||
                    !fields[vm.RecipientFields.ToList().IndexOf(target)].Contains("\"Doe, Jane\""))
                    throw new InvalidOperationException("Saved draft did not preserve the moved recipient and display name.");
            }
            // Same-field and unrelated drops must leave the recipient where it was.
            await Drag(Chip(token), Field(vm.CcField));
            await Drag(Chip(token), subject);
            if (!vm.CcField.Tokens.Contains(token) || subject.Text?.Length > 0)
                throw new InvalidOperationException("Rejected drop moved a contact or pasted it into Subject.");
            vm.To = "Existing <JANE@example.test>";
            var existing = vm.ToField.Tokens.Single();
            await Drag(Chip(token), Field(vm.ToField));
            if (vm.CcField.Tokens.Count != 0 || vm.ToField.Tokens.Count != 1 || !ReferenceEquals(existing, vm.ToField.Tokens[0]))
                throw new InvalidOperationException("Recipient drop did not merge the duplicate destination address.");
            var remove = Chip(existing).GetVisualDescendants().OfType<Button>().Single();
            var point = remove.PointToScreen(new Point(remove.Bounds.Width / 2, remove.Bounds.Height / 2));
            await Input("click", point.X.ToString(), point.Y.ToString());
            if (vm.ToField.Tokens.Count != 0) throw new InvalidOperationException("Recipient remove button stopped working.");
            Console.WriteLine("Recipient dragging passed: chip-click typing focus, all six To/Cc/Bcc moves, pending text, saved drafts, rejected drops, duplicate merging and removal.");
        }
        finally { window.Close(); }

        Border Chip(ComposeRecipientToken value) => window.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Classes.Contains("recipientToken") && ReferenceEquals(border.DataContext, value));
        TextBox Field(ComposeRecipientField field) => window.GetVisualDescendants().OfType<TextBox>()
            .Single(box => ReferenceEquals(box.DataContext, field));
        async Task Drag(Control source, Control target)
        {
            var from = source.PointToScreen(new Point(12, source.Bounds.Height / 2));
            var to = target.PointToScreen(new Point(40, target.Bounds.Height / 2));
            await Input("drag", from.X.ToString(), from.Y.ToString(), to.X.ToString(), to.Y.ToString());
        }
        async Task Click(Control control)
        {
            var point = control.PointToScreen(new Point(12, control.Bounds.Height / 2));
            await Input("click", point.X.ToString(), point.Y.ToString());
        }
        static async Task Input(params string[] args)
        {
            var start = new ProcessStartInfo("python3");
            start.ArgumentList.Add("tools/BetterMail.UiPreview/input.py");
            start.ArgumentList.Add("none");
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException("Input injection failed.");
            await Task.Delay(250);
        }
    }
}

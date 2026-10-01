using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using BetterMail.App;

internal static partial class Program
{
    private static async Task CheckEmailLinksAsync()
    {
        // Fictional loopback redirect trap. Never launch a browser or send a remote request.
        using var stop = new CancellationTokenSource();
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var target = new Uri($"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/redirect");
        var requests = 0;
        var clients = new System.Collections.Concurrent.ConcurrentBag<Task>();
        var serving = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var client = await server.AcceptTcpClientAsync(stop.Token);
                    clients.Add(Respond(client));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        var view = new NativeWebView();
        var launches = new List<Uri>();
        var failLaunch = false;
        _ = new ReadOnlyWebViewLinks(view, uri =>
        {
            launches.Add(uri);
            if (failLaunch) throw new InvalidOperationException("Synthetic unavailable browser");
        });
        var window = new Window { Content = view, Width = 640, Height = 400, Position = new(0, 0),
            WindowDecorations = WindowDecorations.None, WindowStartupLocation = WindowStartupLocation.Manual };
        var html = new MailContentRenderer().RenderDocument($"<a href='{target}' style='display:block;padding:20px'>Fictional email link</a>", isHtml: true);
        try
        {
            window.Show(); window.Activate();
            await Load();
            await ClickAndCheck(1);
            // Explicitly exercise new-window navigation as well as ordinary navigation.
            await view.InvokeScript("document.querySelector('a').target='_blank'");
            await ClickAndCheck(2);
            failLaunch = true;
            await view.InvokeScript("document.querySelector('a').removeAttribute('target')");
            await ClickAndCheck(3);
            failLaunch = false;
            // No user gesture: a redirect or a script must not open another external tab.
            await view.InvokeScript($"location.href={System.Text.Json.JsonSerializer.Serialize(target.AbsoluteUri)}");
            await Task.Delay(400);
            await Check(3);
            // Detach/re-attach must disconnect the old native policy handler.
            window.Content = null;
            await Task.Delay(200);
            window.Content = view;
            await Load();
            await ClickAndCheck(4);
            Console.WriteLine("Email links passed: one launch per click, same email retained, zero redirect requests, popup and failed-launch handling, reattachment.");
        }
        finally
        {
            window.Close();
            stop.Cancel();
            await serving;
            await Task.WhenAll(clients);
            server.Stop();
        }

        async Task Respond(TcpClient client)
        {
            using (client)
            {
                try
                {
                    // Hover may preconnect without navigating; count actual HTTP requests.
                    if (await client.GetStream().ReadAsync(new byte[4096], stop.Token) == 0) return;
                    Interlocked.Increment(ref requests);
                    var response = Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: /another-redirect\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await client.GetStream().WriteAsync(response, stop.Token);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (IOException) { /* WebKit can close an unused speculative connection. */ }
            }
        }

        async Task Load()
        {
            var loaded = new TaskCompletionSource();
            void Completed(object? sender, WebViewNavigationCompletedEventArgs args)
            {
                if (args.IsSuccess) loaded.TrySetResult();
            }
            view.NavigationCompleted += Completed;
            try
            {
                view.NavigateToString(html, new Uri("about:blank"));
                await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await Task.Delay(300);
            }
            finally { view.NavigationCompleted -= Completed; }
        }

        async Task ClickAndCheck(int expected)
        {
            var point = view.PointToScreen(new Point(65, 35));
            var start = new ProcessStartInfo("python3");
            foreach (var arg in new[] { "tools/BetterMail.UiPreview/input.py", "none", "click", point.X.ToString(), point.Y.ToString() }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException("Mouse injection failed.");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (launches.Count < expected && DateTime.UtcNow < deadline) await Task.Delay(50);
            await Task.Delay(300);
            await Check(expected);
        }

        async Task Check(int expected)
        {
            if (launches.Count != expected || launches.Any(uri => uri != target))
                throw new InvalidOperationException($"Expected {expected} external launches; received {launches.Count}.");
            if (Volatile.Read(ref requests) != 0)
                throw new InvalidOperationException("The email WebView contacted the external redirect endpoint.");
            if (await view.InvokeScript("document.body.textContent.includes('Fictional email link')") != "true")
                throw new InvalidOperationException("External navigation replaced the email.");
        }
    }
}

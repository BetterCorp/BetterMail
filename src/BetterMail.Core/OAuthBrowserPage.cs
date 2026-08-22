namespace BetterMail.Core;

public static class OAuthBrowserPage
{
    private const string LogoResourceName = "BetterMail.Core.Assets.BetterMailLogo.png";
    private static readonly string LogoDataUri = LoadLogoDataUri();

    public static string Html(string providerName, bool success)
    {
        var title = success ? "You're connected" : "Connection unsuccessful";
        var message = success
            ? $"{providerName} is now connected to BetterMail. Your mail will begin syncing securely."
            : $"{providerName} could not finish connecting to BetterMail. Return to the app for details and try again.";
        var mark = success ? "✓" : "!";
        var accent = success ? "#55d6a0" : "#ff8a80";
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{{title}} · BetterMail</title>
              <link rel="icon" type="image/png" href="{{LogoDataUri}}">
              <style>
                :root { color-scheme: dark; font-family: "Segoe UI", Inter, system-ui, sans-serif; }
                * { box-sizing: border-box; }
                body { margin: 0; min-height: 100vh; display: grid; place-items: center; padding: 24px; color: #f7f9fc; background: radial-gradient(circle at 20% 10%, #173f68 0, transparent 38%), radial-gradient(circle at 85% 85%, #263a73 0, transparent 34%), #0b1422; }
                main { width: min(100%, 520px); padding: 42px; text-align: center; border: 1px solid rgba(255,255,255,.12); border-radius: 22px; background: rgba(17,29,47,.82); box-shadow: 0 28px 80px rgba(0,0,0,.38); backdrop-filter: blur(18px); }
                .brand { display: inline-flex; align-items: center; gap: 10px; margin-bottom: 30px; font-size: 15px; font-weight: 650; letter-spacing: .02em; color: #dceaff; }
                .logo { width: 30px; height: 30px; border-radius: 7px; box-shadow: 0 8px 22px rgba(69,130,230,.35); }
                .mark { width: 72px; height: 72px; display: grid; place-items: center; margin: 0 auto 22px; border-radius: 50%; color: {{accent}}; background: color-mix(in srgb, {{accent}} 13%, transparent); border: 1px solid color-mix(in srgb, {{accent}} 45%, transparent); font-size: 36px; font-weight: 500; }
                h1 { margin: 0 0 12px; font-size: clamp(28px, 7vw, 38px); line-height: 1.12; letter-spacing: -.035em; }
                p { margin: 0; color: #b9c8dc; font-size: 16px; line-height: 1.65; }
                .hint { margin-top: 28px; padding: 11px 15px; border-radius: 999px; background: rgba(255,255,255,.055); color: #91a6bf; font-size: 13px; }
                @media (prefers-color-scheme: light) { :root { color-scheme: light; } body { color: #172338; background: radial-gradient(circle at 20% 10%, #d9ecff 0, transparent 40%), radial-gradient(circle at 85% 85%, #e4e7ff 0, transparent 36%), #f4f7fb; } main { background: rgba(255,255,255,.86); border-color: rgba(36,74,114,.12); box-shadow: 0 28px 80px rgba(43,72,104,.16); } .brand { color: #21466f; } p { color: #53677f; } .hint { color: #647990; background: rgba(35,70,110,.055); } }
                @media (max-width: 520px) { main { padding: 32px 24px; } }
              </style>
            </head>
            <body>
              <main>
                <div class="brand"><img class="logo" src="{{LogoDataUri}}" alt="">BetterMail</div>
                <div class="mark" aria-hidden="true">{{mark}}</div>
                <h1>{{title}}</h1>
                <p>{{message}}</p>
                <div class="hint">You can close this tab and return to BetterMail.</div>
              </main>
            </body>
            </html>
            """;
    }

    private static string LoadLogoDataUri()
    {
        using var stream = typeof(OAuthBrowserPage).Assembly.GetManifestResourceStream(LogoResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource '{LogoResourceName}'.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return $"data:image/png;base64,{Convert.ToBase64String(buffer.ToArray())}";
    }
}

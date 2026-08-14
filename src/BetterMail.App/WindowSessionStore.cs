using System.Text.Json;

namespace BetterMail.App;

internal sealed record PreviewWindowSession(string MailboxId, string ProviderMessageId);
internal sealed record CalendarEventWindowSession(string AccountId, string CalendarId, string ProviderEventId);
internal sealed record ComposeWindowSession(string DraftId);

internal sealed record WindowSessionState(
    IReadOnlyList<PreviewWindowSession>? PreviewWindows = null,
    IReadOnlyList<CalendarEventWindowSession>? CalendarEventWindows = null,
    IReadOnlyList<ComposeWindowSession>? ComposeWindows = null)
{
    public IReadOnlyList<PreviewWindowSession> Previews => PreviewWindows ?? [];
    public IReadOnlyList<CalendarEventWindowSession> Events => CalendarEventWindows ?? [];
    public IReadOnlyList<ComposeWindowSession> Composes => ComposeWindows ?? [];
}

internal sealed class WindowSessionStore(string dataDirectory)
{
    private readonly string _path = Path.Combine(dataDirectory, "window-session.json");

    public WindowSessionState Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new();
            }

            var json = File.ReadAllText(_path);
            try
            {
                return JsonSerializer.Deserialize<WindowSessionState>(json) ?? new();
            }
            catch (JsonException)
            {
                return new(JsonSerializer.Deserialize<List<PreviewWindowSession>>(json) ?? []);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(WindowSessionState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var normalized = new WindowSessionState(
                state.Previews.Distinct().ToArray(),
                state.Events.Distinct().ToArray(),
                state.Composes.Distinct().ToArray());
            var temporaryPath = _path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(
                normalized,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Window restoration is non-critical when the profile is read-only.
        }
    }
}

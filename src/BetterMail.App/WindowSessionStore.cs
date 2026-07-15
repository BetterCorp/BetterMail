using System.Text.Json;

namespace BetterMail.App;

internal sealed record PreviewWindowSession(string MailboxId, string ProviderMessageId);

internal sealed class WindowSessionStore(string dataDirectory)
{
    private readonly string _path = Path.Combine(dataDirectory, "window-session.json");

    public IReadOnlyList<PreviewWindowSession> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<List<PreviewWindowSession>>(File.ReadAllText(_path)) ?? []
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public void Save(IEnumerable<PreviewWindowSession> sessions)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(
                _path,
                JsonSerializer.Serialize(sessions.Distinct().ToArray(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Window restoration is non-critical when the profile is read-only.
        }
    }
}

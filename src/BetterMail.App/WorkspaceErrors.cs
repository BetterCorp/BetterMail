namespace BetterMail.App;

internal static class WorkspaceErrors
{
    internal static string Describe(Exception error)
    {
        if (error.Message.Contains("(10008)", StringComparison.Ordinal))
            return "OneNote cannot query this document library because it exceeds Microsoft's 5,000-item limit (10008). Cached notes are kept. Open the notebook in OneNote; move it to a smaller library to restore API sync.";
        return error.Message;
    }
}

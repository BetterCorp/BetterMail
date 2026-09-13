namespace BetterMail.App;

public sealed partial class MainWindowViewModel
{
    private IReadOnlyList<PreviewActionSetting>? _previewActionSettings;
    public IReadOnlyList<PreviewActionSetting> PreviewActionSettings => _previewActionSettings ??=
        new (ConversationAction Action, string Label)[]
        {
            (ConversationAction.Reply, "Reply"), (ConversationAction.ReplyAll, "Reply all"),
            (ConversationAction.Forward, "Forward"), (ConversationAction.Archive, "Archive"),
            (ConversationAction.Delete, "Delete"), (ConversationAction.Move, "Move"),
            (ConversationAction.Junk, "Mark as junk"), (ConversationAction.NotJunk, "Mark as not junk"),
            (ConversationAction.ToggleRead, "Mark read / unread"),
            (ConversationAction.ToggleFlag, "Flag / unflag"), (ConversationAction.TogglePin, "Pin / unpin")
        }.Select(item => new PreviewActionSetting(item.Action, item.Label, () =>
        {
            PreviewActionsVersion++;
            RaisePropertyChanged(nameof(PreviewActionsVersion));
        })).ToArray();

    public int PreviewActionsVersion { get; private set; }
    public void ConfigurePreviewActions(IEnumerable<string>? actions)
    {
        var close = (actions ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var setting in PreviewActionSettings)
            setting.Behavior = close.Contains(setting.Action.ToString()) ? "Close" : "Stay open";
    }
    public List<string> GetClosePreviewActions() => PreviewActionSettings
        .Where(item => item.Behavior == "Close").Select(item => item.Action.ToString()).ToList();
    internal bool ShouldClosePreview(ConversationAction action) =>
        PreviewActionSettings.Any(item => item.Action == action && item.Behavior == "Close");
}

public sealed class PreviewActionSetting(ConversationAction action, string label, Action changed) : ViewModelBase
{
    public ConversationAction Action { get; } = action;
    public string Label { get; } = label;
    public IReadOnlyList<string> Behaviors { get; } = ["Stay open", "Close"];
    private string _behavior = "Stay open";
    public string Behavior
    {
        get => _behavior;
        set { if (Behaviors.Contains(value) && SetProperty(ref _behavior, value)) changed(); }
    }
}

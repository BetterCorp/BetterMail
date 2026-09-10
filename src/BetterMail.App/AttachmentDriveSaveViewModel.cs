using BetterMail.Core;

namespace BetterMail.App;

public sealed class AttachmentDriveSaveViewModel : ViewModelBase
{
    private readonly IFilesProvider _provider;
    private readonly byte[] _content;
    private readonly string _contentType;
    private bool _isSaving;
    private bool _isSaved;
    private string? _status;

    public AttachmentDriveSaveViewModel(IFilesProvider provider, IReadOnlyList<MailAccount> accounts,
        string name, string contentType, byte[] content)
    {
        _provider = provider;
        _content = content;
        _contentType = contentType;
        FileName = NormalizeFileName(name);
        var driveAccounts = accounts.Where(account => account.Capabilities.HasFlag(ProviderCapabilities.Files)).ToArray();
        Workspace = new DriveWorkspaceViewModel(provider, driveAccounts);
        Status = driveAccounts.Length == 0 ? "Connect a OneDrive account in Settings to save attachments to Drive." : null;
        Workspace.PropertyChanged += (_, _) => Refresh();
    }

    internal static string NormalizeFileName(string name)
    {
        // Mail attachment names need not follow the destination filesystem's rules.
        // Use a fixed character set so Linux, macOS, and Windows behave identically.
        var clean = string.Concat(name.Select(character =>
            char.IsControl(character) || "<>:\"/\\|?*".Contains(character) ? '_' : character))
            .Trim().TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(clean) ? "attachment" : clean;
    }

    public DriveWorkspaceViewModel Workspace { get; }
    public string FileName { get; }
    public bool IsSaving { get => _isSaving; private set { SetProperty(ref _isSaving, value); Refresh(); } }
    public bool IsSaved { get => _isSaved; private set { SetProperty(ref _isSaved, value); Refresh(); } }
    public string? Status { get => _status; private set => SetProperty(ref _status, value); }
    public string CloseLabel => IsSaving ? "Cancel upload" : IsSaved ? "Done" : "Cancel";
    public bool CanBrowse => !IsSaving && !IsSaved;
    public bool CanSave => CanBrowse && Workspace.SelectedDirectory is { IsLoaded: true, HasError: false, IsLoading: false };

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSave || Workspace.SelectedDirectory is not { } destination)
            return;
        IsSaving = true;
        Status = $"Saving to {destination.PathLabel}…";
        try
        {
            var saved = await Task.Run(async () =>
            {
                using var stream = new MemoryStream(_content, writable: false);
                return await _provider.UploadFileAsync(destination.Account, destination.Item, FileName,
                    stream, _content.LongLength, _contentType, cancellationToken).ConfigureAwait(false);
            }, cancellationToken);
            IsSaved = true;
            Status = $"Saved {saved.Name} to {destination.PathLabel}.";
        }
        catch (OperationCanceledException)
        {
            Status = "Upload stopped. Check the destination before saving again; the file may already have arrived.";
        }
        catch (Exception)
        {
            Status = "Could not confirm the upload. Check the destination before trying again.";
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void Refresh()
    {
        RaisePropertyChanged(nameof(CloseLabel));
        RaisePropertyChanged(nameof(CanBrowse));
        RaisePropertyChanged(nameof(CanSave));
    }
}

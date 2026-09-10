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
    private string _fileName = "";

    public AttachmentDriveSaveViewModel(IFilesProvider provider, IReadOnlyList<MailAccount> accounts,
        string name, string contentType, byte[] content)
    {
        _provider = provider;
        _content = content;
        _contentType = contentType;
        _fileName = NormalizeFileName(name);
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
        if (string.IsNullOrWhiteSpace(clean)) return "attachment";
        // OneDrive also reserves device names and temporary/system filenames.
        // https://support.microsoft.com/en-us/onedrive/restrictions-and-limitations-in-onedrive-and-sharepoint
        clean = clean.Replace("_vti_", "_vti-", StringComparison.OrdinalIgnoreCase);
        var stem = clean.Split('.')[0].TrimEnd().ToUpperInvariant();
        var deviceName = stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                                  stem.StartsWith("LPT", StringComparison.Ordinal)) &&
             stem[3] is >= '0' and <= '9');
        if (deviceName || clean.Equals(".lock", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("~$", StringComparison.Ordinal))
            clean = "_" + clean;
        return clean;
    }

    public DriveWorkspaceViewModel Workspace { get; }
    public string FileName
    {
        get => _fileName;
        set
        {
            if (CanBrowse && SetProperty(ref _fileName, value)) Refresh();
        }
    }
    public string? FileNameError
    {
        get
        {
            if (string.IsNullOrWhiteSpace(FileName)) return "Enter a filename.";
            if (FileName.Length > 255) return "Shorten the filename to 255 characters or fewer, including the extension.";
            if (NormalizeFileName(FileName) != FileName) return "Use a valid Drive filename without reserved names or characters.";
            var pathLength = FileName.Length;
            // Folder names from Graph JSON are already decoded. Count separators and
            // actual names, not URL escapes or the account label shown at the root.
            for (var node = Workspace.SelectedDirectory; node?.Item is { } item; node = node.Parent)
                pathLength += item.Name.Length + 1;
            return pathLength > 400
                ? "The folder path and filename exceed 400 characters. Shorten the filename or choose a folder closer to the root."
                : null;
        }
    }
    public bool IsSaving { get => _isSaving; private set { SetProperty(ref _isSaving, value); Refresh(); } }
    public bool IsSaved { get => _isSaved; private set { SetProperty(ref _isSaved, value); Refresh(); } }
    public string? Status { get => _status; private set => SetProperty(ref _status, value); }
    public string CloseLabel => IsSaving ? "Cancel upload" : IsSaved ? "Done" : "Cancel";
    public bool CanBrowse => !IsSaving && !IsSaved;
    public bool CanSave => CanBrowse && FileNameError is null && !Workspace.IsNavigating && Workspace.SelectedDirectory is { IsLoaded: true, HasError: false, IsLoading: false };

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSave || Workspace.SelectedDirectory is not { } destination)
            return;
        var fileName = FileName;
        IsSaving = true;
        Status = $"Saving to {destination.PathLabel}…";
        try
        {
            var saved = await Task.Run(async () =>
            {
                using var stream = new MemoryStream(_content, writable: false);
                return await _provider.UploadFileAsync(destination.Account, destination.Item, fileName,
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
        RaisePropertyChanged(nameof(FileNameError));
        RaisePropertyChanged(nameof(CloseLabel));
        RaisePropertyChanged(nameof(CanBrowse));
        RaisePropertyChanged(nameof(CanSave));
    }
}

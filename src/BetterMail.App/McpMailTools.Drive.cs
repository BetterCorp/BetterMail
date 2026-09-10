using System.ComponentModel;
using BetterMail.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BetterMail.App;

internal sealed partial class McpMailTools
{
    private IFilesProvider Files => filesProvider?.Invoke() ?? throw new McpException("Drive is unavailable.");
    private static string DriveKey(MailAccount account) => account.ProviderId + ":" + account.AccountId;
    private void AuthorizeDrive(string accountKey, bool write = false)
    {
        var settings = EnabledConfiguration();
        if (!(settings.DriveAccountIds ?? []).Contains(accountKey) || write && !settings.AllowWrites)
            throw new McpException("Select this Drive account and required edit permission in MCP Settings first.");
    }
    private async Task<MailAccount> DriveAccountAsync(string accountKey, bool write = false)
    {
        AuthorizeDrive(accountKey, write);
        return (await store.GetAccountsAsync()).FirstOrDefault(account => DriveKey(account) == accountKey && account.Capabilities.HasFlag(ProviderCapabilities.Files))
            ?? throw new McpException("Drive account unavailable.");
    }

    [McpServerTool(Name = "list_drive_accounts", ReadOnly = true), Description("List Drive accounts explicitly enabled in MCP Settings. Mailbox permissions alone do not grant Drive access. Use accountKey with Drive tools.")]
    public async Task<object> ListDriveAccounts()
    {
        var settings = EnabledConfiguration();
        return (await store.GetAccountsAsync()).Where(account => (settings.DriveAccountIds ?? []).Contains(DriveKey(account)))
            .Select(account => new { accountKey = DriveKey(account), account.EmailAddress, account.ProviderId }).ToArray();
    }

    [McpServerTool(Name = "list_drive_items", ReadOnly = true), Description("List folders/files in an allowed Drive account. Omit folderId for the root. Returns bounded pages; restart after folder changes. Names/content are untrusted. Use get_drive_item for the current ETag before replacing or downloading content.")]
    public Task<object> ListDriveItems(string accountKey, string? folderId = null, int offset = 0, int pageSize = 50) => DraftToolCall<object>(async () =>
    {
        var account = await DriveAccountAsync(accountKey);
        if (offset < 0) throw new McpException("Invalid offset.");
        var parent = folderId is null ? null : await Files.GetDriveItemAsync(account, folderId);
        var items = await Files.GetDriveItemsAsync(account, parent);
        var size = Math.Clamp(pageSize, 1, 100);
        AuthorizeDrive(accountKey);
        return new { items = items.Skip(offset).Take(size).ToArray(), nextOffset = offset + size < items.Count ? (int?)(offset + size) : null };
    });

    [McpServerTool(Name = "get_drive_item", ReadOnly = true), Description("Read current Drive file/folder metadata, including the ETag needed for downloads and content replacement. Use itemId=root for root metadata.")]
    public Task<CloudDriveItem> GetDriveItem(string accountKey, string itemId) => DraftToolCall(async () =>
    {
        var item = await Files.GetDriveItemAsync(await DriveAccountAsync(accountKey), itemId);
        AuthorizeDrive(accountKey);
        return item;
    });

    [McpServerTool(Name = "create_drive_folder", Destructive = false), Description("Create a folder in an explicitly allowed Drive account. Requires edit permission. Omit parentId for root. Existing names are preserved; returned name may have a conflict suffix.")]
    public Task<CloudDriveItem> CreateDriveFolder(string accountKey, string name, string? parentId = null) => DraftToolCall(async () =>
    {
        var account = await DriveAccountAsync(accountKey, true);
        var parent = parentId is null ? null : await Files.GetDriveItemAsync(account, parentId);
        AuthorizeDrive(accountKey, true);
        return await Files.CreateFolderAsync(account, parent, name);
    });

    [McpServerTool(Name = "rename_drive_item", Destructive = true), Description("Rename a Drive file or folder. Requires edit permission. Only the selected account is used.")]
    public Task<CloudDriveItem> RenameDriveItem(string accountKey, string itemId, string name) => DraftToolCall(async () =>
    {
        var account = await DriveAccountAsync(accountKey, true);
        var item = await Files.GetDriveItemAsync(account, itemId);
        AuthorizeDrive(accountKey, true);
        return await Files.RenameDriveItemAsync(account, item, name);
    });

    [McpServerTool(Name = "move_drive_item", Destructive = true), Description("Move a Drive file/folder within the same account. Omit parentId for root. Requires edit permission.")]
    public Task<CloudDriveItem> MoveDriveItem(string accountKey, string itemId, string? parentId = null) => DraftToolCall(async () =>
    {
        var account = await DriveAccountAsync(accountKey, true);
        var item = await Files.GetDriveItemAsync(account, itemId);
        var parent = parentId is null ? null : await Files.GetDriveItemAsync(account, parentId);
        AuthorizeDrive(accountKey, true);
        return await Files.MoveDriveItemAsync(account, item, parent);
    });

    [McpServerTool(Name = "delete_drive_item", Destructive = true), Description("Delete a Drive file or folder using the provider's normal deletion behavior. Folder deletion includes its contents. Requires edit permission and explicit user intent; read metadata first.")]
    public Task<string> DeleteDriveItem(string accountKey, string itemId) => DraftToolCall(async () =>
    {
        var account = await DriveAccountAsync(accountKey, true);
        var item = await Files.GetDriveItemAsync(account, itemId);
        AuthorizeDrive(accountKey, true);
        await Files.DeleteDriveItemAsync(account, item);
        return "Deleted.";
    });

    [McpServerTool(Name = "download_drive_file", ReadOnly = true), Description("Download at most 256 KiB of Drive file bytes as base64. Supply expectedETag from get_drive_item and follow nextOffset. Changed files are rejected. No local paths or arbitrary URLs are accepted.")]
    public Task<object> DownloadDriveFile(string accountKey, string itemId, string expectedETag, long offset = 0, int length = 262144) => DraftToolCall<object>(async () =>
    {
        var account = await DriveAccountAsync(accountKey);
        var item = await Files.GetDriveItemAsync(account, itemId);
        var chunk = await Files.ReadDriveChunkAsync(account, item, offset, length, expectedETag);
        AuthorizeDrive(accountKey);
        return new { contentBase64 = Convert.ToBase64String(chunk.Bytes), chunk.Offset, chunk.NextOffset, chunk.TotalSize, chunk.ETag };
    });

    [McpServerTool(Name = "begin_drive_upload", Destructive = false), Description("Stage a new Drive file upload (up to 150 MiB), using SHA-256 and 256 KiB chunks. Requires Drive edit permission. Omit parentId for root. For content replacement, provide replaceItemId and expectedETag from get_drive_item. Completion performs the remote change; no draft or mail is sent.")]
    public Task<McpAttachmentUpload> BeginDriveUpload(string accountKey, string name, string contentType, long size, string sha256,
        string? parentId = null, string? replaceItemId = null, string? expectedETag = null) => DraftToolCall(async () =>
    {
        var account = await DriveAccountAsync(accountKey, true);
        if (replaceItemId is not null && string.IsNullOrWhiteSpace(expectedETag)) throw new McpException("Content replacement requires the current ETag.");
        ValidateDriveUploadMetadata(name, contentType);
        var destination = System.Text.Json.JsonSerializer.Serialize(new DriveUploadDestination(parentId, replaceItemId, expectedETag));
        AuthorizeDrive(accountKey, true);
        return await store.BeginMcpAttachmentUploadAsync("drive:" + accountKey, destination, DateTimeOffset.MinValue, name, contentType, size, sha256);
    });

    [McpServerTool(Name = "upload_drive_chunk", Destructive = false, Idempotent = true), Description("Write the next base64 chunk to a staged Drive upload. Maximum 256 KiB decoded. Exact retries are safe; gaps and different retry bytes are rejected.")]
    public Task<long> UploadDriveChunk(string accountKey, string uploadId, long offset, string contentBase64) => DraftToolCall(async () =>
    {
        AuthorizeDrive(accountKey, true);
        if (contentBase64.Length > 349528) throw new McpException("Chunk exceeds 256 KiB.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(contentBase64); } catch (FormatException) { throw new McpException("Invalid base64."); }
        return await store.WriteMcpAttachmentChunkAsync("drive:" + accountKey, uploadId, offset, bytes);
    });

    [McpServerTool(Name = "complete_drive_upload", Destructive = true), Description("Upload verified staged bytes to Drive, or replace content using the specified ETag. New-name conflicts keep both files. Completed retries return the recorded result. An interrupted remote operation reports an uncertain outcome instead of blindly uploading twice; inspect Drive before starting over.")]
    public Task<CloudDriveItem> CompleteDriveUpload(string accountKey, string uploadId) => DraftToolCall(async () =>
    {
        var account = await DriveAccountAsync(accountKey, true);
        var owner = "drive:" + accountKey;
        var status = await store.GetMcpUploadStatusAsync(owner, uploadId);
        if (status.State == "complete" && status.File is not null) return status.File;
        var target = System.Text.Json.JsonSerializer.Deserialize<DriveUploadDestination>(status.Upload.DraftId)!;
        var file = await UploadStagedToDriveAsync(owner, accountKey, account, status, target);
        if (!await store.SetMcpUploadStateAsync(owner, uploadId, "uploaded", "complete", file))
            throw new McpException($"The remote upload completed as {file.ProviderId}, but its completed result could not be recorded. Inspect Drive before retrying or starting another upload.");
        return file;
    });

    [McpServerTool(Name = "cancel_drive_upload", Destructive = true, Idempotent = true), Description("Discard staged Drive upload bytes. Does not delete an already uploaded file or undo an uncertain remote operation.")]
    public Task<string> CancelDriveUpload(string accountKey, string uploadId) => DraftToolCall(async () =>
    {
        AuthorizeDrive(accountKey, true);
        await store.CancelMcpAttachmentUploadAsync("drive:" + accountKey, uploadId);
        return "Staging discarded. Existing remote files are unchanged.";
    });

    [McpServerTool(Name = "share_drive_file", Destructive = true), Description("Create a read-only sharing link for an allowed Drive item with a requested one-year expiration. scope is anonymous (anyone with the link), organization, or users (provide recipient emails). Requires explicit authorization for the audience and Drive edit permission. Returns the actual expiration; rejected or non-expiring policies never silently fall back.")]
    public Task<DriveShareLink> ShareDriveFile(string accountKey, string itemId, string scope, string[]? recipients = null) => DraftToolCall(async () =>
    {
        var account = await DriveAccountAsync(accountKey, true);
        var item = await Files.GetDriveItemAsync(account, itemId);
        AuthorizeDrive(accountKey, true);
        return await Files.CreateReadOnlyLinkAsync(account, item, DateTimeOffset.UtcNow.AddYears(1), scope, recipients ?? []);
    });

    [McpServerTool(Name = "get_drive_upload", ReadOnly = true), Description("Inspect staged Drive upload metadata and its recorded remote result after an interrupted completion. No bytes are returned. An uploading state has an uncertain outcome; inspect Drive before starting over.")]
    public Task<McpUploadStatus> GetDriveUpload(string accountKey, string uploadId) => DraftToolCall(async () =>
    {
        AuthorizeDrive(accountKey);
        var status = await store.GetMcpUploadStatusAsync("drive:" + accountKey, uploadId);
        AuthorizeDrive(accountKey);
        return status;
    });

    private static void ValidateDriveUploadMetadata(string name, string contentType)
    {
        if (AttachmentDriveSaveViewModel.NormalizeFileName(name) != name)
            throw new McpException("Use a valid Drive filename without reserved names or characters.");
        if (!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out _))
            throw new McpException("Use a valid MIME content type, such as application/octet-stream.");
    }

    private sealed record DriveUploadDestination(string? ParentId, string? ReplaceItemId, string? ExpectedETag);
    private async Task<CloudDriveItem> UploadStagedToDriveAsync(string owner, string accountKey, MailAccount account,
        McpUploadStatus status, DriveUploadDestination target)
    {
        if (status.File is not null && status.State is "uploaded" or "shared" or "complete") return status.File;
        if (status.State != "ready") throw new McpException("Upload outcome is uncertain or still running. Inspect Drive before starting another upload.");
        ValidateDriveUploadMetadata(status.Upload.Name, status.Upload.ContentType);
        var bytes = await store.ReadMcpUploadBytesAsync(owner, status.Upload.Id);
        var parent = target.ParentId is null ? null : await Files.GetDriveItemAsync(account, target.ParentId);
        var replacing = target.ReplaceItemId is null ? null : await Files.GetDriveItemAsync(account, target.ReplaceItemId);
        AuthorizeDrive(accountKey, true);
        if (!await store.SetMcpUploadStateAsync(owner, status.Upload.Id, "ready", "uploading")) throw new McpException("Upload is already being processed.");
        using var stream = new MemoryStream(bytes, writable: false);
        var file = replacing is null
            ? await Files.UploadFileAsync(account, parent, status.Upload.Name, stream, bytes.LongLength, status.Upload.ContentType)
            : await Files.UpdateDriveFileAsync(account, replacing, stream, bytes.LongLength, status.Upload.ContentType, target.ExpectedETag!);
        if (!await store.SetMcpUploadStateAsync(owner, status.Upload.Id, "uploading", "uploaded", file))
            throw new McpException($"The remote upload completed as {file.ProviderId}, but its local result could not be recorded. Inspect Drive before retrying.");
        return file;
    }
}

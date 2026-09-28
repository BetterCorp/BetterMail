namespace BetterMail.App;

// Owned by the UI thread. Keep the last usable snapshot while local storage refreshes.
internal sealed class RecipientDirectoryCache(
    Func<Task<IReadOnlyList<RecipientSuggestion>>> loadContacts,
    Func<Task<IReadOnlyList<RecipientSuggestion>>> loadHistory)
{
    private IReadOnlyList<RecipientSuggestion>? _snapshot;
    private IReadOnlyList<RecipientSuggestion> _history = [];
    private Task? _refresh;
    private bool _refreshAgain;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event Action? Changed;

    public async Task<IReadOnlyList<RecipientSuggestion>> SearchAsync(string query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_snapshot is null)
        {
            if (_refresh is null) _ = RefreshAsync();
            await _ready.Task.WaitAsync(token);
        }
        return (_snapshot ?? [])
            .Where(item => item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                           item.Address.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Address.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => item.DisplayName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(20).ToArray();
    }

    public Task RefreshAsync()
    {
        if (_refresh is { IsCompleted: false })
        {
            _refreshAgain = true;
            return _refresh;
        }
        return _refresh = RefreshCoreAsync();
    }

    private async Task RefreshCoreAsync()
    {
        do
        {
            _refreshAgain = false;
            try
            {
                var contacts = await loadContacts();
                Publish(contacts);
                // Publish saved contacts first: autocomplete must not wait for a mail-history aggregate.
                _history = await loadHistory();
                Publish(contacts);
            }
            catch (Exception exception)
            {
                // A failed refresh must neither discard known recipients nor fault every keystroke.
                System.Diagnostics.Trace.TraceWarning("Local recipient directory refresh failed: {0}", exception.GetType().Name);
                _snapshot ??= [];
                _ready.TrySetResult();
            }
        } while (_refreshAgain);
    }

    private void Publish(IReadOnlyList<RecipientSuggestion> contacts)
    {
        _snapshot = contacts.Concat(_history)
            .Where(static item => !string.IsNullOrWhiteSpace(item.Address))
            .DistinctBy(static item => item.Address, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _ready.TrySetResult();
        Changed?.Invoke();
    }
}

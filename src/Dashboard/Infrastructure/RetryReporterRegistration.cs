namespace AzureFinOps.Dashboard.Infrastructure;

internal sealed class RetryReporterRegistration : IDisposable
{
    private readonly object _sync = new();
    private string? _key;
    private Func<int, double, string, string, int, Task>? _report;
    private bool _disposed;

    internal void Bind(string key, Func<int, double, string, string, int, Task> report)
    {
        lock (_sync)
        {
            if (_disposed) return;
            RemoveCurrent();
            _key = key;
            _report = report;
            HttpHelper.RetryReporters[key] = report;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            RemoveCurrent();
        }
    }

    private void RemoveCurrent()
    {
        if (_key is not null && _report is not null)
            ((ICollection<KeyValuePair<string, Func<int, double, string, string, int, Task>>>)HttpHelper.RetryReporters)
                .Remove(new(_key, _report));
        _key = null;
        _report = null;
    }
}
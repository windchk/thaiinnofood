using Microsoft.Extensions.Options;
using OdooSapApi.Models;

namespace OdooSapApi.Services;

public sealed class SapDiApiExecutionGate
{
    private readonly SemaphoreSlim _semaphore;
    private readonly TimeSpan _acquireTimeout;
    private readonly int _retryAfterSeconds;

    public SapDiApiExecutionGate(IOptions<SapDiApiExecutionOptions> options)
    {
        var value = options.Value;
        var maxConcurrency = Math.Max(1, value.MaxConcurrency);
        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _acquireTimeout = value.AcquireTimeoutSeconds == Timeout.Infinite
            ? Timeout.InfiniteTimeSpan
            : TimeSpan.FromSeconds(Math.Max(0, value.AcquireTimeoutSeconds));
        _retryAfterSeconds = Math.Max(1, value.RetryAfterSeconds);
    }

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        if (!await _semaphore.WaitAsync(_acquireTimeout, cancellationToken))
        {
            throw new SapDiApiBusyException(
                "SAP DI API is busy. Retry the request later.",
                _retryAfterSeconds);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new Lease(_semaphore);
        }
        catch
        {
            _semaphore.Release();
            throw;
        }
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }
}

public sealed class SapDiApiBusyException : Exception
{
    public SapDiApiBusyException(string message, int retryAfterSeconds)
        : base(message)
    {
        RetryAfterSeconds = retryAfterSeconds;
    }

    public int RetryAfterSeconds { get; }
}

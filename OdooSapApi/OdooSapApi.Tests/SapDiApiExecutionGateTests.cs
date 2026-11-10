using Microsoft.Extensions.Options;
using OdooSapApi.Models;
using OdooSapApi.Services;
using Xunit;

namespace OdooSapApi.Tests;

public class SapDiApiExecutionGateTests
{
    [Fact]
    public async Task EnterAsync_WhenSingleSlotIsBusy_FailsFastWithRetryAfter()
    {
        var gate = CreateGate(acquireTimeoutSeconds: 0, retryAfterSeconds: 15);
        using var firstLease = await gate.EnterAsync();

        var exception = await Assert.ThrowsAsync<SapDiApiBusyException>(
            () => gate.EnterAsync());

        Assert.Equal(15, exception.RetryAfterSeconds);
    }

    [Fact]
    public async Task EnterAsync_AfterLeaseIsReleased_AllowsNextOperation()
    {
        var gate = CreateGate(acquireTimeoutSeconds: 0, retryAfterSeconds: 15);
        var firstLease = await gate.EnterAsync();

        firstLease.Dispose();

        using var secondLease = await gate.EnterAsync();
    }

    [Fact]
    public async Task EnterAsync_WithTwoSlots_AllowsTwoAndRejectsThird()
    {
        var gate = CreateGate(
            acquireTimeoutSeconds: 0,
            retryAfterSeconds: 15,
            maxConcurrency: 2);
        using var firstLease = await gate.EnterAsync();
        using var secondLease = await gate.EnterAsync();

        await Assert.ThrowsAsync<SapDiApiBusyException>(
            () => gate.EnterAsync());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task EnterAsync_WithInfiniteTimeout_WaitsForAnAvailableSlot(int maxConcurrency)
    {
        var gate = CreateGate(-1, 15, maxConcurrency);
        var leases = new List<IDisposable>();
        using var cancellation = new CancellationTokenSource();
        try
        {
            for (var i = 0; i < maxConcurrency; i++)
            {
                leases.Add(await gate.EnterAsync());
            }

            var waiting = gate.EnterAsync(cancellation.Token);
            Assert.False(waiting.IsCompleted);

            leases[0].Dispose();
            using var nextLease = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            cancellation.Cancel();
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    [Fact]
    public async Task EnterAsync_WhenCanceledWhileWaiting_DoesNotStartWorkOrReleaseAnotherLease()
    {
        var gate = CreateGate(-1, 15);
        using var firstLease = await gate.EnterAsync();
        using var cancellation = new CancellationTokenSource();
        var workStarted = false;

        async Task WaitingOperation()
        {
            using var lease = await gate.EnterAsync(cancellation.Token);
            workStarted = true;
        }

        var waitingOperation = WaitingOperation();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingOperation);
        Assert.False(workStarted);

        using var nextCancellation = new CancellationTokenSource();
        var nextWaiting = gate.EnterAsync(nextCancellation.Token);
        try
        {
            Assert.False(nextWaiting.IsCompleted);
            firstLease.Dispose();
            using var nextLease = await nextWaiting.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            nextCancellation.Cancel();
        }
    }

    [Fact]
    public async Task EnterAsync_WhenAlreadyCanceled_DoesNotConsumeAvailableSlot()
    {
        var gate = CreateGate(-1, 15);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.EnterAsync(cancellation.Token));

        using var availableLease = await gate.EnterAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task EnterAsync_WithInfiniteTimeout_RunsAllWaitingOperationsOnceWithinConcurrencyLimit()
    {
        var gate = CreateGate(-1, 15, maxConcurrency: 2);
        using var firstLease = await gate.EnterAsync();
        using var secondLease = await gate.EnterAsync();
        using var cancellation = new CancellationTokenSource();
        var releaseWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new int[5];
        var active = 0;
        var peakActive = 0;
        var sync = new object();
        var operations = Enumerable.Range(0, calls.Length).Select(async index =>
        {
            using var lease = await gate.EnterAsync(cancellation.Token);
            lock (sync)
            {
                active++;
                peakActive = Math.Max(peakActive, active);
                calls[index]++;
            }

            await releaseWork.Task;

            lock (sync)
            {
                active--;
            }
        }).ToArray();

        try
        {
            Assert.All(operations, operation => Assert.False(operation.IsCompleted));
            Assert.All(calls, count => Assert.Equal(0, count));

            firstLease.Dispose();
            secondLease.Dispose();
            releaseWork.SetResult();
            await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.All(calls, count => Assert.Equal(1, count));
            Assert.InRange(peakActive, 1, 2);
        }
        finally
        {
            cancellation.Cancel();
            releaseWork.TrySetResult();
        }
    }

    [Fact]
    public async Task EnterAsync_WhenFiniteWaitIsConfigured_StillTimesOutWithRetryAfter()
    {
        var gate = CreateGate(acquireTimeoutSeconds: 1, retryAfterSeconds: 15);
        using var firstLease = await gate.EnterAsync();

        var exception = await Assert.ThrowsAsync<SapDiApiBusyException>(
            () => gate.EnterAsync());

        Assert.Equal(15, exception.RetryAfterSeconds);
    }

    [Fact]
    public async Task EnterAsync_WithDefaultCapacity_AllowsSixOperationsAndTimesOutSeventh()
    {
        // Use a shorter wait to exercise the same timeout path without waiting a minute.
        var gate = new SapDiApiExecutionGate(Options.Create(new SapDiApiExecutionOptions
        {
            AcquireTimeoutSeconds = 1
        }));
        var leases = new List<IDisposable>();
        try
        {
            for (var i = 0; i < 6; i++)
            {
                leases.Add(await gate.EnterAsync());
            }

            var seventhOperation = gate.EnterAsync();
            Assert.False(seventhOperation.IsCompleted);
            var exception = await Assert.ThrowsAsync<SapDiApiBusyException>(() => seventhOperation);
            Assert.Equal(15, exception.RetryAfterSeconds);

            leases[0].Dispose();
            using var nextLease = await gate.EnterAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    private static SapDiApiExecutionGate CreateGate(
        int acquireTimeoutSeconds,
        int retryAfterSeconds,
        int maxConcurrency = 1)
    {
        return new SapDiApiExecutionGate(Options.Create(new SapDiApiExecutionOptions
        {
            MaxConcurrency = maxConcurrency,
            AcquireTimeoutSeconds = acquireTimeoutSeconds,
            RetryAfterSeconds = retryAfterSeconds
        }));
    }
}

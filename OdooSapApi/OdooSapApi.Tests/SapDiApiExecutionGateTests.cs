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

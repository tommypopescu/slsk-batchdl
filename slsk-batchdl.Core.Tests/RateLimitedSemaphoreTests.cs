using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.Core;

[TestClass]
public class RateLimitedSemaphoreTests
{
    [TestMethod]
    public async Task WaitAsync_WhenCancelled_ThrowsImmediately()
    {
        // Give it 1 permit that takes 10 seconds to replenish.
        var semaphore = new RateLimitedSemaphore(1, TimeSpan.FromSeconds(10));
        
        // Consume the only permit
        await semaphore.WaitAsync();

        using var cts = new CancellationTokenSource();
        var waitTask = semaphore.WaitAsync(cts.Token);

        // Verify it's actually blocking
        Assert.IsFalse(waitTask.IsCompleted, "WaitAsync should block when the rate limit is exhausted.");

        // Cancel the token
        cts.Cancel();

        // If the fix works, this will throw an OperationCanceledException immediately,
        // rather than hanging for 10 seconds.
        var ex = await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () => await waitTask);
        Assert.IsNotNull(ex);
    }
}
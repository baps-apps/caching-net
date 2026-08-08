namespace Caching.NET.Tests.Integration.Fixtures;

/// <summary>
/// A rendezvous for stampede tests: every caller's factory reports its arrival and then blocks until
/// the test releases it, so "one factory execution" is observed rather than raced for.
/// </summary>
/// <remarks>
/// <para>
/// The obvious way to wait for the first factory — <c>while (Volatile.Read(ref calls) == 0) await
/// Task.Yield();</c> — has no deadline and no way of noticing that every caller has already failed.
/// When the callers fault (a broken registration, an unreachable Redis) the condition becomes
/// permanently false and the loop re-queues itself onto the thread pool forever. The pool's
/// hill-climbing heuristic reads that as work starvation and injects more threads, so a single stuck
/// test grows from one busy core to several and the run never terminates. This gate replaces the
/// spin with a completion source, a deadline, and an explicit check that the callers are still
/// running.
/// </para>
/// <para>
/// Both waits are fenced. Arrival is not the only place a caller can hang: one that stalls after the
/// factory has run would hang a bare <c>Task.WhenAll</c> just as permanently, so
/// <see cref="RunAsync{TValue}"/> owns the completion wait too rather than leaving it to each test.
/// </para>
/// </remarks>
internal sealed class FactoryGate : IDisposable
{
    private static readonly TimeSpan ArrivalTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(30);

    private readonly TaskCompletionSource _firstArrival =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly SemaphoreSlim _release = new(0);
    private int _executions;

    /// <summary>How many factory executions have started.</summary>
    public int Executions => Volatile.Read(ref _executions);

    /// <summary>Called from inside a factory: records the execution and waits for the release.</summary>
    public async Task EnterAsync(CancellationToken token = default)
    {
        if (Interlocked.Increment(ref _executions) == 1)
        {
            _firstArrival.TrySetResult();
        }

        await _release.WaitAsync(token);
    }

    /// <summary>
    /// Waits until the first factory has started, releases every waiting factory, and returns the
    /// callers' results. Every wait is bounded: a mass fault, a key that was already cached, and a
    /// caller stalling after the factory ran all fail the test instead of hanging the run.
    /// </summary>
    /// <typeparam name="TValue">The callers' result type.</typeparam>
    /// <param name="callers">The in-flight caller tasks.</param>
    public async Task<TValue[]> RunAsync<TValue>(IReadOnlyCollection<Task<TValue>> callers)
    {
        var allCallers = Task.WhenAll(callers);

        using (var arrivalTimeout = new CancellationTokenSource())
        {
            // Cancelled on the way out so the happy path does not leave a 30-second timer alive.
            var timeout = Task.Delay(ArrivalTimeout, arrivalTimeout.Token);
            var finished = await Task.WhenAny(_firstArrival.Task, allCallers, timeout);
            await arrivalTimeout.CancelAsync();

            if (finished == allCallers)
            {
                // Surfaces the callers' own exception when there is one, rather than a timeout.
                await allCallers;
                throw new InvalidOperationException(
                    "Every caller completed without the factory ever running. The key was already cached.");
            }

            if (finished == timeout)
            {
                throw new TimeoutException(
                    $"No factory execution started within {ArrivalTimeout}. The callers are stuck or already failed.");
            }
        }

        _release.Release(callers.Count);

        return await allCallers.WaitAsync(CompletionTimeout);
    }

    public void Dispose() => _release.Dispose();
}

namespace Rmc.Engine;

/// <summary>
/// Shares the machine between concurrent heavy requests (meld solves, upgrade plans): at most
/// <see cref="MaxConcurrentJobs"/> run at once, each with its share of the threads, and the rest
/// wait their turn. A job's thread share is what it passes as MaxDegreeOfParallelism.
/// </summary>
public sealed class ComputePool(int totalThreads, int maxConcurrentJobs)
{
    private readonly SemaphoreSlim slots = new(maxConcurrentJobs, maxConcurrentJobs);
    private int waiting;

    public int TotalThreads { get; } = totalThreads;
    public int MaxConcurrentJobs { get; } = maxConcurrentJobs;
    /// <summary>Threads each running job may use.</summary>
    public int ThreadsPerJob => Math.Max(1, TotalThreads / MaxConcurrentJobs);
    /// <summary>Jobs queued behind the running ones.</summary>
    public int Waiting => Volatile.Read(ref waiting);

    /// <summary>The machine's threads with a few jobs at a time: big enough per job to finish a solve fast.</summary>
    public static ComputePool ForThisMachine(int maxConcurrentJobs = 4) => new(Environment.ProcessorCount, maxConcurrentJobs);

    public async Task<T> RunAsync<T>(Func<int, T> work, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref waiting);
        try
        {
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref waiting);
        }
        try
        {
            return await Task.Run(() => work(ThreadsPerJob), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            slots.Release();
        }
    }
}

namespace VlakoveCedule.Net;

/// <summary>Vložení SemaphoreSlim.WaitAsync jako rozšíření, aby šlo volání čitelně řetězit.</summary>
internal static class SemaphoreSlimExtensions
{
    public static async Task<T> RunAsync<T>(this SemaphoreSlim sem, Func<Task<T>> factory, CancellationToken ct)
    {
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try { return await factory().ConfigureAwait(false); }
        finally { sem.Release(); }
    }

    public static async Task RunAsync(this SemaphoreSlim sem, Func<Task> factory, CancellationToken ct)
    {
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try { await factory().ConfigureAwait(false); }
        finally { sem.Release(); }
    }
}
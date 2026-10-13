namespace EventCapture.App.Services;

internal static class StartupReplayRecovery
{
    public static async Task RunAsync(Func<Task> start, Func<bool> requested,
        Action<int, Exception> retry, CancellationToken token,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        for (int attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await start();
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < 4 && requested())
            {
                retry(attempt, ex);
                await delay(TimeSpan.FromSeconds(attempt * 3), token);
            }
        }
    }
}

namespace TheoriaCast.Visual.Api;

internal sealed class VisualWorkQueue
{
    internal const int MaxAttempts = 3;
    private readonly HashSet<string> suspended = new(StringComparer.Ordinal);

    public static bool Pending(string status) => status is "queued" or "running";

    public bool CanRun(string key) => !suspended.Contains(key);

    public async Task Process(string key, int attempts, Action<int> claim, Func<Task> run,
        Action<string> fail, string failureMessage, ILogger logger, CancellationToken stoppingToken)
    {
        try
        {
            stoppingToken.ThrowIfCancellationRequested();
            if (attempts >= MaxAttempts)
                throw new VisualConflict($"処理の試行回数が上限の {MaxAttempts} 回に達しました。");
            claim(Math.Max(0, attempts) + 1);
            await run();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            var message = error switch
            {
                VisualConflict or InvalidDataException or ArgumentException => error.Message,
                OperationCanceledException => "処理が途中で取り消されました。",
                KeyNotFoundException => "処理に必要なレシピまたは入力が見つかりません。",
                _ => failureMessage
            };
            logger.LogWarning("Visual work {Work} failed ({Type})", key, error.GetType().Name);
            try { fail(message); }
            catch (Exception saveError)
            {
                // An unwritable record must not block subsequent work or spin until the process restarts.
                suspended.Add(key);
                logger.LogError("Visual work {Work} is suspended because its state could not be saved ({Type})",
                    key, saveError.GetType().Name);
            }
        }
    }

    public static async Task Run(Func<CancellationToken, Task<bool>> step, Func<CancellationToken, Task> idle,
        ILogger logger, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await step(stoppingToken))
                    await idle(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogError("Could not advance the Visual queue ({Type})", error.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }
}

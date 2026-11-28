using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using TheoriaCast.Visual.Api;

KaraokeSceneTests.Run();
VerifyComposition();
await VerifyQueue();

static void VerifyComposition()
{
    using var foreground = new Mat(20, 40, MatType.CV_8UC4, new Scalar(240, 120, 0, 128));
    using var transposed = new Mat(40, 20, MatType.CV_8UC4);
    using var missingAlpha = new Mat(20, 40, MatType.CV_8UC3);
    using var background = new Mat(20, 40, MatType.CV_8UC4, new Scalar(20, 60, 200, 255));
    Reject(transposed);
    Reject(missingAlpha);
    using var result = VisualImages.Composite(foreground, background);
    var pixel = result.At<Vec4b>(0, 0);
    Require(pixel.Item0 == 130 && pixel.Item1 == 90 && pixel.Item2 == 100 && pixel.Item3 == 255,
        "Source-over pixel is incorrect.");
    Console.WriteLine("PASS: composition validates equal dimensions and four channels before blending.");

    void Reject(Mat other)
    {
        try
        {
            using var unexpected = VisualImages.Composite(foreground, other);
            throw new InvalidOperationException("Unexpected composition success.");
        }
        catch (InvalidDataException error)
        {
            Require(error.Message.Contains("寸法"), "Composition did not report the mismatch.");
        }
    }
}

static async Task VerifyQueue()
{
    var queue = new VisualWorkQueue();
    var logger = NullLogger.Instance;
    int claimed = 0;
    string? failure = null;
    void Claim(int attempts) => claimed = attempts;
    void Fail(string details) => failure = details;

    await queue.Process("timeout", 0, Claim, () => throw new OperationCanceledException(), Fail,
        "fallback", logger, CancellationToken.None);
    Require(claimed == 1 && failure?.Contains("取り消されました") == true, "A provider cancellation must fail the item.");

    claimed = 0;
    failure = null;
    await queue.Process("exhausted", VisualWorkQueue.MaxAttempts, Claim,
        () => throw new InvalidOperationException("Run must not be invoked."), Fail, "fallback", logger, CancellationToken.None);
    Require(claimed == 0 && failure?.Contains("上限") == true, "An exhausted item must not be claimed.");

    await queue.Process("unwritable", 0, _ => throw new IOException(), () => Task.CompletedTask,
        _ => throw new IOException(), "fallback", logger, CancellationToken.None);
    Require(!queue.CanRun("unwritable"), "An unwritable item must be suspended.");

    using var stop = new CancellationTokenSource();
    failure = null;
    try
    {
        await queue.Process("shutdown", 1, Claim,
            () => { stop.Cancel(); throw new OperationCanceledException(stop.Token); }, Fail, "fallback", logger, stop.Token);
        throw new InvalidOperationException("A shutdown must interrupt work.");
    }
    catch (OperationCanceledException)
    {
        Require(claimed == 2 && failure is null, "Shutdown must preserve the recorded attempt for recovery.");
    }

    await queue.Process("healthy", 0, Claim, () => Task.CompletedTask, Fail, "fallback", logger, CancellationToken.None);
    Require(claimed == 1 && failure is null, "The next healthy item must run after poison items.");
    Console.WriteLine("PASS: queue isolates cancellation and write errors, records claims, and stops at the attempt limit.");
}

static void Require(bool condition, string error)
{
    if (!condition) throw new InvalidOperationException(error);
}

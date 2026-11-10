using GlitchPilot.Core.Models;
using GlitchPilot.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GlitchPilot.Core.Tests;

public class ErrorClassifierTests
{
    private sealed class FailingChat(Exception ex) : IChatCompleter
    {
        public Task<string> CompleteAsync(string systemInstruction, string userPrompt, CancellationToken ct = default) =>
            Task.FromException<string>(ex);
    }

    private static TriageCandidate Candidate() => new() { ErrorId = "1", ErrorTitle = "Boom" };

    [Fact]
    public async Task Timeout_ReturnsFallbackVerdict()
    {
        var classifier = new ErrorClassifier(new FailingChat(new TaskCanceledException("timeout")),
            NullLogger<ErrorClassifier>.Instance);

        var verdict = await classifier.EvaluateAsync(Candidate());

        Assert.False(verdict.RequiresAction);
        Assert.Contains("Classification unavailable", verdict.Explanation);
    }

    [Fact]
    public async Task RealCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var classifier = new ErrorClassifier(new FailingChat(new OperationCanceledException(cts.Token)),
            NullLogger<ErrorClassifier>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => classifier.EvaluateAsync(Candidate(), cts.Token));
    }

    [Fact]
    public async Task ParsesVerdictFromChat()
    {
        var chat = new FakeChat("""{"requiresAction":true,"priority":"high","errorType":"bug","issueTitle":"T"}""");
        var classifier = new ErrorClassifier(chat, NullLogger<ErrorClassifier>.Instance);

        var verdict = await classifier.EvaluateAsync(Candidate());

        Assert.True(verdict.RequiresAction);
        Assert.Equal("high", verdict.Priority);
        Assert.Contains("Title: Boom", Assert.Single(chat.Calls).User);
    }
}

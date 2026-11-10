namespace GlitchPilot.Core.Services;

/// <summary>Minimal abstraction over the Azure OpenAI / Foundry chat endpoint.</summary>
public interface IChatCompleter
{
    Task<string> CompleteAsync(string systemInstruction, string userPrompt, CancellationToken ct = default);
}

using Azure;
using Azure.AI.OpenAI;
using GlitchPilot.Core.Configuration;
using OpenAI.Chat;

namespace GlitchPilot.Core.Services;

public sealed class AzureChatCompleter : IChatCompleter
{
    private readonly ChatClient _chat;

    public AzureChatCompleter()
    {
        var aoai = new AzureOpenAIClient(
            new Uri(EnvironmentConfig.OpenAiEndpoint),
            new AzureKeyCredential(EnvironmentConfig.OpenAiApiKey));
        _chat = aoai.GetChatClient(EnvironmentConfig.OpenAiModel);
    }

    public async Task<string> CompleteAsync(string systemInstruction, string userPrompt, CancellationToken ct = default)
    {
        // No temperature / ResponseFormat / max_tokens: not supported by all deployments (e.g. gpt-5-mini).
        var completion = await _chat.CompleteChatAsync(
            [new SystemChatMessage(systemInstruction), new UserChatMessage(userPrompt)],
            cancellationToken: ct);
        return completion.Value.Content[0].Text;
    }
}

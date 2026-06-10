using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WpfAppTest.Providers;

internal class ChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = new();

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 4096;

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 0.3;
}

internal class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    public object? Content { get; set; }
}

internal class ChatResponse
{
    [JsonPropertyName("choices")]
    public List<Choice>? Choices { get; set; }
}

internal class Choice
{
    [JsonPropertyName("message")]
    public ChatMessageContent? Message { get; set; }
}

internal class ChatMessageContent
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

internal class ModelsResponse
{
    [JsonPropertyName("data")]
    public List<ModelEntry>? Data { get; set; }
}

internal class ModelEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

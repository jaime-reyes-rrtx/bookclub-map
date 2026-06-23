namespace RAG.Core.Services;

public sealed class AiProviderFactory(
    IEmbeddingProvider embeddings,
    IChatCompletionProvider chat) : IAiProviderFactory
{
    public IEmbeddingProvider Embeddings { get; } = embeddings;
    public IChatCompletionProvider Chat { get; } = chat;
}

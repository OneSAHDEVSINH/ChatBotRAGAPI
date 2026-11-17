namespace ChatBotRAGAPI.Contracts;

/// <param name="Private">True when chatting needs the knowledge base's key (X-Chat-Key).</param>
/// <param name="Documents">How many documents it holds.</param>
public sealed record KnowledgeBaseInfo(string Id, string Name, bool Private, int Documents);

/// <param name="Id">1-40 lower-case letters, digits or hyphens; used in API calls, the chat URL (?kb=) and the widget.</param>
/// <param name="Name">A display name (optional).</param>
/// <param name="Private">Require a chat key. The key is returned once, in the response.</param>
public sealed record KnowledgeBaseRequest(string Id, string? Name = null, bool Private = false);

/// <param name="Name">New display name (optional).</param>
/// <param name="Private">Switch to private (a key is issued) or public (the key stops working).</param>
/// <param name="NewKey">Issue a new key for a private knowledge base; the old one stops working.</param>
public sealed record KnowledgeBaseUpdate(string? Name = null, bool? Private = null, bool NewKey = false);

/// <param name="ChatKey">Only present when a key was just issued. Store it now: it cannot be shown again.</param>
public sealed record KnowledgeBaseResponse(KnowledgeBaseInfo KnowledgeBase, string? ChatKey);

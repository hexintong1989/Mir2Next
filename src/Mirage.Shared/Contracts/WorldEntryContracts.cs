namespace Mirage.Shared.Contracts;

public sealed record WorldEntryTicketRequest(string? RealmId);

public sealed record WorldEntryTicketResponse(
    Guid CharacterId,
    string RealmId,
    string WebSocketUrl,
    string Ticket,
    DateTimeOffset ExpiresAt);

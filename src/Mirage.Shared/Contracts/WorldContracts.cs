namespace Mirage.Shared.Contracts;

public sealed record MapResponse(string MapId, int Width, int Height, int PlayerCount);

public sealed record JoinWorldRequest(Guid CharacterId, int X, int Y);

public sealed record MoveRequest(long Sequence, int X, int Y);

public sealed record PlayerPositionResponse(
    Guid CharacterId,
    int X,
    int Y,
    long LastProcessedSequence);

public sealed record MoveResponse(
    bool Accepted,
    string? Rejection,
    PlayerPositionResponse Player);

namespace Mirage.Domain.World;

public enum PartyFailure
{
    None,
    PlayerNotFound,
    TargetNotFound,
    StaleSequence,
    InvalidTarget,
    AlreadyInParty,
    TargetAlreadyInParty,
    NotLeader,
    InviteNotFound,
    InviteExpired,
    InvitePending,
    PartyFull,
    NotInParty
}

public sealed record PartySnapshot(
    Guid PartyId,
    Guid LeaderCharacterId,
    IReadOnlyList<Guid> MemberCharacterIds);

public readonly record struct PartyInviteResult(
    PartyFailure Failure,
    WorldPlayer Player,
    Guid TargetCharacterId,
    long ExpiresAtTick)
{
    public bool IsSuccess => Failure == PartyFailure.None;
}

public readonly record struct PartyChangeResult(
    PartyFailure Failure,
    WorldPlayer Player,
    PartySnapshot? Party,
    IReadOnlyList<Guid> AffectedCharacterIds)
{
    public bool IsSuccess => Failure == PartyFailure.None;
}

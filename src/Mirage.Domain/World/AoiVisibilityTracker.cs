namespace Mirage.Domain.World;

public sealed record AoiDelta(
    IReadOnlyList<WorldPlayer> Entered,
    IReadOnlyList<WorldPlayer> Updated,
    IReadOnlyList<Guid> Left)
{
    public bool HasChanges => Entered.Count > 0 || Updated.Count > 0 || Left.Count > 0;
}

public sealed class AoiVisibilityTracker
{
    private HashSet<Guid> _visibleCharacterIds = [];

    public AoiDelta Update(IReadOnlyList<WorldPlayer> visiblePlayers, Guid? changedCharacterId = null)
    {
        var currentIds = visiblePlayers.Select(player => player.CharacterId).ToHashSet();
        var entered = visiblePlayers
            .Where(player => !_visibleCharacterIds.Contains(player.CharacterId))
            .ToArray();
        var left = _visibleCharacterIds
            .Where(characterId => !currentIds.Contains(characterId))
            .Order()
            .ToArray();
        var updated = changedCharacterId is { } changed
            ? visiblePlayers
                .Where(player => player.CharacterId == changed && _visibleCharacterIds.Contains(changed))
                .ToArray()
            : [];

        _visibleCharacterIds = currentIds;
        return new AoiDelta(entered, updated, left);
    }
}

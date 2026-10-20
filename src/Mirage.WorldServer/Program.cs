using Mirage.Domain.World;
using Mirage.Shared.Contracts;
using Mirage.WorldServer;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(new WorldInstance("local-field", 128, 128));
builder.Services.AddSingleton<WorldSimulation>();
builder.Services.AddHostedService<WorldTickService>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "Mirage.WorldServer",
    protocolVersion = 1,
    utc = DateTimeOffset.UtcNow
}));

app.MapGet("/api/v1/maps", (WorldInstance world) => Results.Ok(new[]
{
    new MapResponse(world.MapId, world.Width, world.Height, world.PlayerCount)
}));

if (app.Environment.IsDevelopment())
{
    app.MapPost("/internal/dev/players", (JoinWorldRequest request, WorldInstance world) =>
    {
        var result = world.Join(request.CharacterId, new GridPosition(request.X, request.Y));
        return result.IsSuccess
            ? Results.Created(
                $"/internal/dev/players/{request.CharacterId}",
                ToPositionResponse(result.Player!))
            : Results.Conflict(new { code = result.Failure.ToString() });
    });

    app.MapPost("/internal/dev/players/{characterId:guid}/move", async (
        Guid characterId,
        MoveRequest request,
        WorldSimulation simulation,
        CancellationToken cancellationToken) =>
    {
        var result = await simulation.QueueMoveAsync(
            characterId,
            request.Sequence,
            new GridPosition(request.X, request.Y),
            cancellationToken);
        return Results.Ok(new MoveResponse(
            result.IsSuccess,
            result.IsSuccess ? null : result.Failure.ToString(),
            ToPositionResponse(result.Player)));
    });

    app.MapGet("/internal/dev/players", (WorldInstance world) =>
        Results.Ok(world.Snapshot().Select(ToPositionResponse)));

    app.MapGet("/internal/dev/players/{characterId:guid}/visible", (
        Guid characterId,
        int? radius,
        WorldInstance world) =>
    {
        var requestedRadius = radius ?? 12;
        return requestedRadius is < 1 or > 64
            ? Results.BadRequest(new { code = "InvalidRadius" })
            : Results.Ok(world.VisibleTo(characterId, requestedRadius).Select(ToPositionResponse));
    });
}

app.Run();

static PlayerPositionResponse ToPositionResponse(WorldPlayer player) =>
    new(
        player.CharacterId,
        player.Position.X,
        player.Position.Y,
        player.LastProcessedSequence);

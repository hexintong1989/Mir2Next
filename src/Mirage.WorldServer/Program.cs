using Mirage.Domain.Characters;
using Mirage.Domain.World;
using Mirage.Persistence.Postgres;
using Mirage.Shared.Contracts;
using Mirage.WorldServer;

var builder = WebApplication.CreateBuilder(args);
var world = new WorldInstance("local-field", 128, 128);
world.SpawnMonster(
    Guid.Parse("10000000-0000-0000-0000-000000000001"),
    "training_dummy",
    new GridPosition(66, 64),
    1,
    30,
    "wooden_token");
world.SpawnMonster(
    Guid.Parse("10000000-0000-0000-0000-000000000002"),
    "forest_deer",
    new GridPosition(68, 65),
    1,
    40,
    "venison");
world.SpawnMonster(
    Guid.Parse("10000000-0000-0000-0000-000000000003"),
    "scarecrow",
    new GridPosition(63, 67),
    2,
    50,
    "cloth_scrap");
builder.Services.AddSingleton(world);
builder.Services.AddSingleton<WorldSimulation>();
builder.Services.AddHostedService<WorldTickService>();
builder.Services.AddSingleton<WorldSocketHub>();
builder.Services.AddSingleton<WorldEntryTicketReplayGuard>();

var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(postgresConnectionString))
{
    builder.Services.AddSingleton<ICharacterWorldStateStore, InMemoryCharacterWorldStateStore>();
    builder.Services.AddSingleton<ICharacterInventoryStore, InMemoryCharacterInventoryStore>();
}
else
{
    var dataSource = PostgresStorage.CreateDataSource(postgresConnectionString);
    builder.Services.AddSingleton(dataSource);
    builder.Services.AddSingleton<PostgresCharacterRepository>();
    builder.Services.AddSingleton<ICharacterWorldStateStore>(services =>
        services.GetRequiredService<PostgresCharacterRepository>());
    builder.Services.AddSingleton<ICharacterInventoryStore>(services =>
        services.GetRequiredService<PostgresCharacterRepository>());
}

var ticketProtector = CreateTicketProtector(builder.Configuration);
if (ticketProtector is not null)
{
    builder.Services.AddSingleton(ticketProtector);
}

var app = builder.Build();
if (!string.IsNullOrWhiteSpace(postgresConnectionString)
    && builder.Configuration.GetValue<bool>("Database:ApplyMigrations"))
{
    await PostgresMigrationRunner.ApplyAsync(
        app.Services.GetRequiredService<Npgsql.NpgsqlDataSource>(),
        app.Lifetime.ApplicationStopping);
}
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "Mirage.WorldServer",
    protocolVersion = 1,
    utc = DateTimeOffset.UtcNow
}));

app.MapGet("/api/v1/maps", (WorldInstance world) => Results.Ok(new[]
{
    new MapResponse(world.MapId, world.Width, world.Height, world.PlayerCount, world.MonsterCount)
}));

app.Map("/ws/v1/world", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var protector = context.RequestServices.GetService<HmacWorldEntryTicketProtector>();
    if (protector is null)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return;
    }

    var validation = protector.Validate(
        context.Request.Query["ticket"],
        "local",
        DateTimeOffset.UtcNow);
    if (!validation.IsSuccess)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    var replayGuard = context.RequestServices.GetRequiredService<WorldEntryTicketReplayGuard>();
    if (!replayGuard.TryRedeem(validation.Claims!, DateTimeOffset.UtcNow))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    if (!string.Equals(validation.Claims!.MapId, "local-field", StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        return;
    }

    var hub = context.RequestServices.GetRequiredService<WorldSocketHub>();
    await hub.AcceptAsync(
        context,
        validation.Claims!.CharacterId,
        new GridPosition(validation.Claims.PositionX, validation.Claims.PositionY),
        validation.Claims.Archetype,
        true,
        context.RequestAborted);
});

if (app.Environment.IsDevelopment())
{
    app.Map("/ws/v1/world/dev", async context =>
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!Guid.TryParse(context.Request.Query["characterId"], out var characterId)
            || !int.TryParse(context.Request.Query["x"], out var x)
            || !int.TryParse(context.Request.Query["y"], out var y))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var hub = context.RequestServices.GetRequiredService<WorldSocketHub>();
        await hub.AcceptAsync(
            context,
            characterId,
            new GridPosition(x, y),
            CharacterArchetype.Warrior,
            false,
            context.RequestAborted);
    });

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

    app.MapPost("/internal/dev/players/{characterId:guid}/attack", async (
        Guid characterId,
        AttackRequest request,
        WorldSimulation simulation,
        CancellationToken cancellationToken) =>
    {
        var result = await simulation.QueueAttackAsync(
            characterId,
            request.Sequence,
            request.MonsterId,
            cancellationToken);
        return Results.Ok(new AttackResponse(
            result.IsSuccess,
            result.IsSuccess ? null : result.Failure.ToString(),
            result.Damage,
            result.Defeated,
            result.Monster is null ? null : ToMonsterResponse(result.Monster)));
    });

    app.MapPost("/internal/dev/players/{characterId:guid}/pickup", async (
        Guid characterId,
        PickupRequest request,
        WorldSimulation simulation,
        CancellationToken cancellationToken) =>
    {
        var result = await simulation.QueuePickupAsync(
            characterId,
            request.Sequence,
            request.DropId,
            cancellationToken);
        return Results.Ok(new PickupResponse(
            result.IsSuccess,
            result.IsSuccess ? null : result.Failure.ToString(),
            result.Drop is null ? null : ToDropResponse(result.Drop),
            result.InventoryItem is null
                ? null
                : new InventoryItemResponse(result.InventoryItem.ItemId, result.InventoryItem.Quantity)));
    });

    app.MapGet("/internal/dev/players/{characterId:guid}/inventory", (
        Guid characterId,
        WorldInstance world) =>
        Results.Ok(world.InventorySnapshot(characterId)
            .Select(item => new InventoryItemResponse(item.ItemId, item.Quantity))));

    app.MapGet("/internal/dev/drops", (WorldInstance world) =>
        Results.Ok(world.DropSnapshot().Select(ToDropResponse)));

    app.MapPost("/internal/dev/monsters", (
        SpawnMonsterRequest request,
        WorldInstance world) =>
    {
        var spawned = world.SpawnMonster(
            request.MonsterId,
            request.TemplateId,
            new GridPosition(request.X, request.Y),
            request.Level,
            request.MaximumHealth);
        return spawned
            ? Results.Created(
                $"/internal/dev/monsters/{request.MonsterId}",
                new MonsterResponse(
                    request.MonsterId,
                    request.TemplateId,
                    request.X,
                    request.Y,
                    request.Level,
                    request.MaximumHealth,
                    request.MaximumHealth))
            : Results.Conflict(new { code = "MonsterSpawnRejected" });
    });

    app.MapGet("/internal/dev/monsters", (WorldInstance world) =>
        Results.Ok(world.MonsterSnapshot().Select(ToMonsterResponse)));

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
        player.LastProcessedSequence,
        player.Health,
        player.MaximumHealth,
        player.IsAlive,
        player.InvulnerableUntilTick,
        player.Archetype,
        player.AttackPower,
        player.Defense,
        player.AttackCooldownTicks);

static MonsterResponse ToMonsterResponse(WorldMonster monster) =>
    new(
        monster.MonsterId,
        monster.TemplateId,
        monster.Position.X,
        monster.Position.Y,
        monster.Level,
        monster.Health,
        monster.MaximumHealth);

static GroundDropResponse ToDropResponse(WorldDrop drop) =>
    new(
        drop.DropId,
        drop.ItemId,
        drop.Quantity,
        drop.Position.X,
        drop.Position.Y,
        drop.OwnerCharacterId,
        drop.OwnerProtectionUntilTick,
        drop.ExpiresAtTick);

static HmacWorldEntryTicketProtector? CreateTicketProtector(IConfiguration configuration)
{
    var encodedKey = configuration["WorldEntryTickets:SigningKeyBase64"];
    if (string.IsNullOrWhiteSpace(encodedKey))
    {
        return null;
    }

    try
    {
        return new HmacWorldEntryTicketProtector(Convert.FromBase64String(encodedKey));
    }
    catch (FormatException)
    {
        throw new InvalidOperationException("World entry ticket signing key is not valid Base64.");
    }
}

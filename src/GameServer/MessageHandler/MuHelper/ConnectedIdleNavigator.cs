// <copyright file="ConnectedIdleNavigator.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MessageHandler.MuHelper;

using System.Threading;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// Guides a connected browser client to level-appropriate hunting grounds.
/// Combat and walking remain client-driven, so closing the connection stops all activity.
/// </summary>
internal sealed class ConnectedIdleNavigator : IAsyncDisposable
{
    private const int DestinationPacketLength = 8;
    private const int MaximumLevelLead = 5;
    private const int MapChangeImprovement = 6;
    private const int GroundScatterRadius = 6;
    private const int DestinationReachedRange = 5;
    private const int MaximumPointAttempts = 20;

    private static readonly TimeSpan EvaluationInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MapChangeCooldown = TimeSpan.FromMinutes(2);
    private static readonly WarpAction WarpAction = new();

    private readonly RemotePlayer _player;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly PeriodicTimer _timer = new(EvaluationInterval);
    private Task? _runTask;
    private DateTime _nextMapChangeUtc = DateTime.MinValue;
    private Point? _destination;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectedIdleNavigator"/> class.
    /// </summary>
    /// <param name="player">The connected browser player.</param>
    public ConnectedIdleNavigator(RemotePlayer player)
    {
        this._player = player;
    }

    /// <summary>
    /// Starts evaluating suitable hunting grounds.
    /// </summary>
    public void Start() => this._runTask = this.RunAsync(this._cancellation.Token);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        this._timer.Dispose();
        await this._cancellation.CancelAsync().ConfigureAwait(false);
        if (this._runTask is { } runTask)
        {
            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when browser navigation is stopped or disconnected.
            }
        }

        this._cancellation.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await this.EvaluateAsync().ConfigureAwait(false);
        while (await this._timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await this.EvaluateAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask EvaluateAsync()
    {
        if (this._player.Connection is not { Connected: true }
            || this._player.PlayerState.CurrentState != PlayerState.EnteredWorld
            || this._player.CurrentMap is not { } currentMap)
        {
            return;
        }

        if (DateTime.UtcNow >= this._nextMapChangeUtc
            && this.TryPickBetterMap(currentMap.Definition, out var warp))
        {
            this._nextMapChangeUtc = DateTime.UtcNow + MapChangeCooldown;
            this._destination = null;
            await WarpAction.WarpToAsync(this._player, warp).ConfigureAwait(false);
            return;
        }

        if (this._destination is not { } destination
            || this.Distance(this._player.Position, destination) <= DestinationReachedRange)
        {
            if (!this.TryPickGround(currentMap, out destination))
            {
                return;
            }

            this._destination = destination;
        }

        await this.SendDestinationAsync(currentMap.Definition.Number, destination).ConfigureAwait(false);
    }

    private bool TryPickBetterMap(GameMapDefinition currentMap, out WarpInfo warp)
    {
        warp = default!;
        var currentDifference = this.BestLevelDifference(currentMap);
        var bestDifference = currentDifference;
        var level = this._player.Level;

        foreach (var candidate in this._player.GameContext.Configuration.WarpList)
        {
            var map = candidate.Gate?.Map;
            if (map is null || map.Number == currentMap.Number || candidate.Costs > this._player.Money)
            {
                continue;
            }

            if (this._player.SelectedCharacter is not { } character
                || character.GetEffectiveMoveLevelRequirement(candidate.LevelRequirement) > level
                || map.TryGetRequirementError(this._player, out _))
            {
                continue;
            }

            var difference = this.BestLevelDifference(map);
            if (difference != int.MaxValue
                && (bestDifference == int.MaxValue || difference + MapChangeImprovement < bestDifference))
            {
                bestDifference = difference;
                warp = candidate;
            }
        }

        return warp is not null;
    }

    private int BestLevelDifference(GameMapDefinition map)
    {
        var level = this._player.Level;
        return this.PermanentMonsterSpawns(map)
            .Select(area => this.MonsterLevel(area.MonsterDefinition!))
            .Where(monsterLevel => monsterLevel <= level + MaximumLevelLead)
            .Select(monsterLevel => Math.Abs(level - monsterLevel))
            .DefaultIfEmpty(int.MaxValue)
            .Min();
    }

    private bool TryPickGround(GameMap map, out Point destination)
    {
        destination = default;
        var level = this._player.Level;
        var candidates = this.PermanentMonsterSpawns(map.Definition)
            .Select(area => (Area: area, Level: this.MonsterLevel(area.MonsterDefinition!)))
            .Where(candidate => candidate.Level <= level + MaximumLevelLead)
            .OrderBy(candidate => Math.Abs(level - candidate.Level))
            .ThenByDescending(candidate => candidate.Area.Quantity)
            .ToList();

        foreach (var candidate in candidates)
        {
            if (this.TryPickWalkablePoint(map, candidate.Area, out destination))
            {
                return true;
            }
        }

        return false;
    }

    private IEnumerable<MonsterSpawnArea> PermanentMonsterSpawns(GameMapDefinition map)
        => map.MonsterSpawns.Where(area => area is
        {
            Quantity: > 0,
            SpawnTrigger: SpawnTrigger.Automatic,
            MonsterDefinition.ObjectKind: NpcObjectKind.Monster,
        });

    private int MonsterLevel(MonsterDefinition monster)
        => (int)(monster.Attributes.FirstOrDefault(attribute => attribute.AttributeDefinition == Stats.Level)?.Value ?? 0);

    private bool TryPickWalkablePoint(GameMap map, MonsterSpawnArea area, out Point point)
    {
        var minimumX = Math.Min(area.X1, area.X2);
        var maximumX = Math.Max(area.X1, area.X2);
        var minimumY = Math.Min(area.Y1, area.Y2);
        var maximumY = Math.Max(area.Y1, area.Y2);
        var isSingleSpot = maximumX - minimumX < GroundScatterRadius
            && maximumY - minimumY < GroundScatterRadius;
        var center = new Point((byte)((minimumX + maximumX) / 2), (byte)((minimumY + maximumY) / 2));
        for (var attempt = 0; attempt < MaximumPointAttempts; attempt++)
        {
            var candidate = isSingleSpot
                ? map.Terrain.GetRandomCoordinate(center, GroundScatterRadius)
                : new Point(
                    (byte)Rand.NextInt(minimumX, maximumX + 1),
                    (byte)Rand.NextInt(minimumY, maximumY + 1));
            if (map.Terrain.WalkMap[candidate.X, candidate.Y]
                && !map.Terrain.SafezoneMap[candidate.X, candidate.Y])
            {
                point = candidate;
                return true;
            }
        }

        point = default;
        return false;
    }

    private async ValueTask SendDestinationAsync(short mapNumber, Point destination)
    {
        if (this._player.Connection is not { } connection)
        {
            return;
        }

        int WritePacket()
        {
            var packet = connection.Output.GetSpan(DestinationPacketLength)[..DestinationPacketLength];
            packet[0] = 0xC1;
            packet[1] = DestinationPacketLength;
            packet[2] = 0xFA;
            packet[3] = (byte)mapNumber;
            packet[4] = destination.X;
            packet[5] = destination.Y;
            var monsterLevel = this.ClosestMonsterLevel(this._player.CurrentMap!.Definition);
            packet[6] = (byte)monsterLevel;
            packet[7] = (byte)(monsterLevel >> 8);
            return DestinationPacketLength;
        }

        await connection.SendAsync(WritePacket).ConfigureAwait(false);
    }

    private int ClosestMonsterLevel(GameMapDefinition map)
    {
        var level = this._player.Level;
        return this.PermanentMonsterSpawns(map)
            .Select(area => this.MonsterLevel(area.MonsterDefinition!))
            .Where(monsterLevel => monsterLevel <= level + MaximumLevelLead)
            .OrderBy(monsterLevel => Math.Abs(level - monsterLevel))
            .FirstOrDefault();
    }

    private int Distance(Point first, Point second)
        => Math.Abs(first.X - second.X) + Math.Abs(first.Y - second.Y);
}

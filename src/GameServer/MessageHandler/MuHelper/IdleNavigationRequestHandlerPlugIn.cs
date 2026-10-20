// <copyright file="IdleNavigationRequestHandlerPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MessageHandler.MuHelper;

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Enables connected navigation for the browser idle client.
/// </summary>
[PlugIn]
[Guid("D768AE2D-655B-4F55-B3CF-54BEE38E4328")]
internal sealed class IdleNavigationRequestHandlerPlugIn : IPacketHandlerPlugIn
{
    private const byte PacketCode = 0xFA;
    private const int StateOffset = 3;
    private static readonly ConcurrentDictionary<Guid, ConnectedIdleNavigator> Navigators = new();

    /// <inheritdoc />
    public bool IsEncryptionExpected => false;

    /// <inheritdoc />
    public byte Key => PacketCode;

    /// <inheritdoc />
    public async ValueTask HandlePacketAsync(Player player, Memory<byte> packet)
    {
        if (player is not RemotePlayer remotePlayer
            || remotePlayer.Connection is not { } connection
            || packet.Length <= StateOffset)
        {
            return;
        }

        if (packet.Span[StateOffset] == 0)
        {
            if (Navigators.TryRemove(connection.Id, out var navigator))
            {
                await navigator.DisposeAsync().ConfigureAwait(false);
            }

            return;
        }

        if (Navigators.ContainsKey(connection.Id))
        {
            return;
        }

        var newNavigator = new ConnectedIdleNavigator(remotePlayer);
        if (Navigators.TryAdd(connection.Id, newNavigator))
        {
            connection.Disconnected += async () =>
            {
                if (Navigators.TryRemove(connection.Id, out var disconnectedNavigator))
                {
                    await disconnectedNavigator.DisposeAsync().ConfigureAwait(false);
                }
            };
            newNavigator.Start();
        }
        else
        {
            await newNavigator.DisposeAsync().ConfigureAwait(false);
        }
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P;

internal sealed class InvalidPeerRequestException(string message, DisconnectReason reason = DisconnectReason.BreachOfProtocol) : SubprotocolException(message)
{
    internal DisconnectReason Reason { get; } = reason;
}

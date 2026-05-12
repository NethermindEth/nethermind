// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Exceptions;

namespace Nethermind.Network.P2P.Subprotocols
{
    public class SubprotocolException(string message) : PeerProtocolException(message)
    {
    }
}

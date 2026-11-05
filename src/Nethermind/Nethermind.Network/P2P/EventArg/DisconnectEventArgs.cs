// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.EventArg
{
    public class DisconnectEventArgs(DisconnectReason disconnectReason, DisconnectType disconnectType, string details) : System.EventArgs
    {
        public DisconnectReason DisconnectReason { get; } = disconnectReason;

        public DisconnectType DisconnectType { get; } = disconnectType;

        public string Details { get; } = details;
    }
}

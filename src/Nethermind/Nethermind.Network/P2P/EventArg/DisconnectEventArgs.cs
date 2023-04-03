// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.EventArg
{
    public class DisconnectEventArgs : System.EventArgs
    {
        public DisconnectReason DisconnectReason { get; }

        public DisconnectType DisconnectType { get; }

        public string Details { get; }

        public DisconnectEventArgs(DisconnectReason disconnectReason, DisconnectType disconnectType, string details)
        {
            DisconnectReason = disconnectReason;
            DisconnectType = disconnectType;
            Details = details;
        }
    }
}

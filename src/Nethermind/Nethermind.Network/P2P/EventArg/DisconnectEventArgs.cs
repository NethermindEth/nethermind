// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.EventArg
{
    public class DisconnectEventArgs : System.EventArgs
    {
        public EthDisconnectReason EthDisconnectReason { get; }

        public DisconnectType DisconnectType { get; }

        public string Details { get; }

        public DisconnectEventArgs(EthDisconnectReason ethDisconnectReason, DisconnectType disconnectType, string details)
        {
            EthDisconnectReason = ethDisconnectReason;
            DisconnectType = disconnectType;
            Details = details;
        }
    }
}

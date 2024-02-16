// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Network.P2P.EventArg
{
    public class ProtocolEventArgs : System.EventArgs
    {
        public IList<(string ProtocolCode, int Version)> Protocols { get; }

        public ProtocolEventArgs(IList<(string ProtocolCode, int Version)> protocols)
        {
            Protocols = protocols;
        }
    }
}

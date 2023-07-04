// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.EventArg
{
    public class ProtocolEventArgs : System.EventArgs
    {
        public int Version { get; }

        public string ProtocolCode { get; }

        public ProtocolEventArgs(string protocolCode, int version)
        {
            Version = version;
            ProtocolCode = protocolCode;
        }
    }
}

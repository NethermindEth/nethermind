// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.EventArg
{
    public class ProtocolEventArgs(string protocolCode, int version) : System.EventArgs
    {
        public int Version { get; } = version;

        public string ProtocolCode { get; } = protocolCode;
    }
}

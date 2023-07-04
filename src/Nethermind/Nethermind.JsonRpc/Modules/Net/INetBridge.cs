// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.JsonRpc.Modules.Net
{
    public interface INetBridge
    {
        Address LocalAddress { get; }
        string LocalEnode { get; }
        ulong NetworkId { get; }
        int PeerCount { get; }
    }
}

// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Network.Discovery.Portal.History.Rpc.Model;

public class PingResult
{
    public ulong EnrSeq { get; set; }
    public UInt256 DataRadius { get; set; }
}

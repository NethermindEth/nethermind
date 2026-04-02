// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Xdc;

public class EpochNumInfo
{
    public Hash256? EpochBlockHash { get; set; }
    public UInt256? EpochRound { get; set; }
    public UInt256? EpochFirstBlockNumber { get; set; }
    public UInt256? EpochLastBlockNumber { get; set; }
    public string? EpochConsensusVersion { get; set; }
}

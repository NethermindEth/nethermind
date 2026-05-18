// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Xdc;

public class PublicApiMissedRoundsMetadata
{
    public ulong EpochRound { get; set; }
    public UInt256? EpochBlockNumber { get; set; }
    public MissedRoundInfo[]? MissedRounds { get; set; }
}

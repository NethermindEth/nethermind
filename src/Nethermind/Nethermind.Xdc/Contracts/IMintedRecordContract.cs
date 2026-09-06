// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc.Contracts;

/// <summary>Running totals the reward upgrade records for one epoch.</summary>
/// <param name="Minted">Cumulative amount minted up to and including the epoch, in Wei.</param>
/// <param name="Burned">Cumulative amount burned up to and including the epoch, in Wei.</param>
/// <param name="RewardBlockNumber">Block at which the epoch's rewards were paid out.</param>
public readonly record struct MintedRecordAccounting(UInt256 Minted, UInt256 Burned, UInt256 RewardBlockNumber);

public interface IMintedRecordContract
{
    void UpdateAccounting(
        ITransactionProcessor transactionProcessor,
        XdcBlockHeader header,
        IXdcReleaseSpec spec,
        UInt256 mintedInEpoch,
        UInt256 burnedInEpoch);

    /// <summary>Reads the first epoch for which the reward upgrade recorded accounting.</summary>
    /// <returns><see langword="false"/> when the reward upgrade has never run, leaving no accounting to read.</returns>
    bool TryGetOnsetEpoch(IWorldState worldState, out UInt256 onsetEpoch);

    /// <summary>Reads the accounting recorded for <paramref name="epoch"/>; unwritten epochs read as zero.</summary>
    MintedRecordAccounting GetEpochAccounting(IWorldState worldState, UInt256 epoch);
}

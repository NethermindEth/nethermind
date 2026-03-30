// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc.Contracts;

public class MintedRecordContract : IMintedRecordContract
{
    private static readonly Address MintedRecordAddress = Address.FromNumber(0x9a);
    private static readonly UInt256 MintedRecordOnsetEpochSlot = UInt256.One;
    private static readonly UInt256 MintedRecordOnsetBlockSlot = (UInt256)2;
    private static readonly UInt256 MintedRecordPostMintedBase = UInt256.Parse("0x0100000000000000000000000000000000000000000000000000000000000000");
    private static readonly UInt256 MintedRecordPostBurnedBase = UInt256.Parse("0x0200000000000000000000000000000000000000000000000000000000000000");
    private static readonly UInt256 MintedRecordPostRewardBlockBase = UInt256.Parse("0x0300000000000000000000000000000000000000000000000000000000000000");

    public void UpdateAccounting(
        ITransactionProcessor transactionProcessor,
        XdcBlockHeader header,
        IXdcReleaseSpec spec,
        UInt256 mintedInEpoch,
        UInt256 burnedInEpoch)
    {
        XdcTransactionProcessor xdcTransactionProcessor = (XdcTransactionProcessor)transactionProcessor;
        IWorldState worldState = xdcTransactionProcessor.RewardWorldState;

        UInt256 epochNumber = (ulong)spec.SwitchEpoch + (header.ExtraConsensusData?.BlockRound ?? 0) / (ulong)spec.EpochLength;
        UInt256 blockNumber = (UInt256)header.Number;

        worldState.CreateAccountIfNotExists(MintedRecordAddress, UInt256.Zero);

        UInt256 totalMinted = UInt256.Zero;
        UInt256 totalBurned = UInt256.Zero;
        UInt256 nonce = worldState.GetNonce(MintedRecordAddress);

        if (nonce.IsZero)
        {
            WriteStorage(worldState, MintedRecordOnsetEpochSlot, epochNumber);
            WriteStorage(worldState, MintedRecordOnsetBlockSlot, blockNumber);
        }
        else
        {
            UInt256 epochNumberIter = epochNumber;
            while (!epochNumberIter.IsZero)
            {
                epochNumberIter -= UInt256.One;
                totalMinted = ReadStorage(worldState, MintedRecordPostMintedBase + epochNumberIter);
                totalBurned = ReadStorage(worldState, MintedRecordPostBurnedBase + epochNumberIter);
                if (!totalMinted.IsZero || !totalBurned.IsZero)
                {
                    break;
                }
            }
        }

        totalMinted = AddSaturating(totalMinted, mintedInEpoch);
        totalBurned = AddSaturating(totalBurned, burnedInEpoch);
        WriteStorage(worldState, MintedRecordPostMintedBase + epochNumber, totalMinted);
        WriteStorage(worldState, MintedRecordPostBurnedBase + epochNumber, totalBurned);
        WriteStorage(worldState, MintedRecordPostRewardBlockBase + epochNumber, blockNumber);
        worldState.IncrementNonce(MintedRecordAddress, UInt256.One, out _);
    }

    private static UInt256 ReadStorage(IWorldState worldState, UInt256 slot)
    {
        ReadOnlySpan<byte> value = worldState.Get(new StorageCell(MintedRecordAddress, slot));
        if (value.Length == 0)
        {
            return UInt256.Zero;
        }

        return new UInt256(value);
    }

    private static void WriteStorage(IWorldState worldState, UInt256 slot, in UInt256 value)
    {
        worldState.Set(new StorageCell(MintedRecordAddress, slot), ToStorageBytes(value));
    }

    private static byte[] ToStorageBytes(in UInt256 value)
    {
        if (value.IsZero)
        {
            return [];
        }

        Span<byte> full = stackalloc byte[32];
        value.ToBigEndian(full);
        int firstNonZero = 0;
        while (firstNonZero < full.Length && full[firstNonZero] == 0)
        {
            firstNonZero++;
        }

        int length = full.Length - firstNonZero;
        byte[] compact = new byte[length];
        full.Slice(firstNonZero).CopyTo(compact);
        return compact;
    }

    private static UInt256 AddSaturating(UInt256 left, UInt256 right)
    {
        return UInt256.AddOverflow(left, right, out UInt256 result) ? UInt256.MaxValue : result;
    }
}

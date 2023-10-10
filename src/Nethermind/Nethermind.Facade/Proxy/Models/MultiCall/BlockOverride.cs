// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Facade.Proxy.Models.MultiCall;

public class BlockOverride
{
    public ulong? Number { get; set; }
    public Keccak PrevRandao { get; set; } = Keccak.Zero;
    public ulong? Time { get; set; }
    public ulong? GasLimit { get; set; }
    public Address? FeeRecipient { get; set; }
    public UInt256? BaseFeePerGas { get; set; }

    public BlockHeader GetBlockHeader(BlockHeader parent, IBlocksConfig cfg)
    {
        ulong newTime = Time ?? checked(parent.Timestamp + cfg.SecondsPerSlot);

        long newGasLimit = GasLimit switch
        {
            null => parent.GasLimit,
            <= long.MaxValue => (long)GasLimit,
            _ => throw new OverflowException($"GasLimit value is too large, max value {ulong.MaxValue}")
        };

        long newBlockNumber = Number switch
        {
            null => checked(parent.Number + 1),
            <= long.MaxValue => (long)Number,
            _ => throw new OverflowException($"Block Number value is too large, max value {ulong.MaxValue}")
        };

        Address newFeeRecipientAddress = FeeRecipient ?? parent.Beneficiary!;
        UInt256 newDifficulty = parent.Difficulty == 0 ? 0 : parent.Difficulty + 1;
        BlockHeader? result = new(
            parent.Hash!,
            Keccak.OfAnEmptySequenceRlp,
            newFeeRecipientAddress,
            newDifficulty,
            newBlockNumber,
            newGasLimit,
            newTime,
            Array.Empty<byte>())
        {
            BaseFeePerGas = BaseFeePerGas ?? parent.BaseFeePerGas,
            MixHash = PrevRandao,
            IsPostMerge = parent.Difficulty == 0,
            TotalDifficulty = parent.TotalDifficulty + newDifficulty,
            SealEngineType = parent.SealEngineType
        };

        return result;
    }
}

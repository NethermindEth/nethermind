// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Facade.Proxy.Models.MultiCall;

public class BlockOverride
{
    public Keccak PrevRandao { get; set; } = Keccak.Zero;
    public UInt256 Number { get; set; }
    public UInt256 Time { get; set; }
    public ulong GasLimit { get; set; }
    public Address FeeRecipient { get; set; } = Address.Zero;
    public UInt256 BaseFee { get; set; }

    public BlockHeader GetBlockHeader(BlockHeader parent, IBlocksConfig cfg)
    {
        ulong newTime = parent.Timestamp + cfg.SecondsPerSlot;
        if (0 == Time) { }
        else if (Time <= ulong.MaxValue)
            newTime = (ulong)Time;
        else
            throw new OverflowException("Time value is too large to be converted to ulong we use.");

        long newGasLimit = parent.GasLimit;
        if (0 == GasLimit) { }
        else if (GasLimit <= long.MaxValue)
            newGasLimit = (long)GasLimit;
        else
            throw new OverflowException("GasLimit value is too large to be converted to long we use.");

        long newBlockNumber = parent.Number + 1;
        if (0 == Number) { }
        else if (Number <= long.MaxValue)
            newBlockNumber = (long)Number;
        else
            throw new OverflowException("Block Number value is too large to be converted to long we use.");

        Address newFeeRecipientAddress = parent.Beneficiary;
        if (FeeRecipient != Address.Zero)
        {
            newFeeRecipientAddress = FeeRecipient;
        }

        var result = new BlockHeader(
            parent.Hash,
            Keccak.OfAnEmptySequenceRlp,
            newFeeRecipientAddress,
            UInt256.Zero,
            newBlockNumber,
            newGasLimit,
            newTime,
            Array.Empty<byte>());

        result.MixHash = PrevRandao;

        //Note we treet parent block as such 
        result.BaseFeePerGas = BaseFee != 0 ? BaseFee : parent.BaseFeePerGas;

        UInt256 difficulty = ConstantDifficulty.One.Calculate(result, parent);
        result.Difficulty = difficulty;
        result.TotalDifficulty = parent.TotalDifficulty + difficulty;
        return result;
    }
}

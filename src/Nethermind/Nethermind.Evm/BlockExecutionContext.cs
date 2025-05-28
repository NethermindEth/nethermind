// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm;

public readonly struct BlockExecutionContext(BlockHeader blockHeader, in UInt256 blobBaseFee)
{
    public readonly BlockHeader Header = blockHeader;
    public readonly Address Coinbase = blockHeader.GasBeneficiary ?? Address.Zero;
    public readonly ulong Number = (ulong)blockHeader.Number;
    public readonly ulong GasLimit = (ulong)blockHeader.GasLimit;
    public readonly ValueHash256 BlobBaseFee = blobBaseFee.ToValueHash();

    // Use the random value if post-merge; otherwise, use block difficulty.
    public readonly ValueHash256 PrevRandao = blockHeader.IsPostMerge
        ? (blockHeader.Random ?? Hash256.Zero).ValueHash256
        : blockHeader.Difficulty.ToValueHash();

    public readonly bool IsGenesis = blockHeader.IsGenesis;

    public BlockExecutionContext(BlockHeader blockHeader, IReleaseSpec spec) : this(blockHeader, GetBlobBaseFee(blockHeader, spec))
    {
    }

    private static UInt256 GetBlobBaseFee(BlockHeader? blockHeader, IReleaseSpec spec) =>
        blockHeader?.ExcessBlobGas is not null
            ? !BlobGasCalculator.TryCalculateFeePerBlobGas(blockHeader.ExcessBlobGas.Value, spec.BlobBaseFeeUpdateFraction, out UInt256 feePerBlobGas)
                ? throw new OverflowException("Blob gas price calculation led to overflow.")
                : feePerBlobGas
            : UInt256.Zero;
}

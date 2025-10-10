// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm;

public readonly struct BlockExecutionContext
{
    public readonly BlockHeader Header;
    public readonly Address Coinbase;
    public readonly ulong Number;
    public readonly ulong GasLimit;
    public readonly ValueHash256 BlobBaseFee;
    public readonly IReleaseSpec Spec;

    // Use the random value if post-merge; otherwise, use block difficulty.
    public readonly ValueHash256 PrevRandao;

    public readonly bool IsGenesis;

    public BlockExecutionContext(BlockHeader blockHeader, IReleaseSpec spec) : this(blockHeader, spec, GetBlobBaseFee(blockHeader, spec))
    {
    }

    public BlockExecutionContext(BlockHeader blockHeader, IReleaseSpec spec, in UInt256 blobBaseFee)
    {
        Out.Log($"transaction block execution context eip3860={spec.IsEip3860Enabled}");

        Header = blockHeader;
        Coinbase = blockHeader.GasBeneficiary ?? Address.Zero;
        Number = (ulong)blockHeader.Number;
        GasLimit = (ulong)blockHeader.GasLimit;
        BlobBaseFee = blobBaseFee.ToValueHash();
        Spec = spec;
        PrevRandao = blockHeader.IsPostMerge
            ? (blockHeader.Random ?? Hash256.Zero).ValueHash256
            : blockHeader.Difficulty.ToValueHash();
        IsGenesis = blockHeader.IsGenesis;
    }

    private static UInt256 GetBlobBaseFee(BlockHeader? blockHeader, IReleaseSpec spec) =>
        blockHeader?.ExcessBlobGas is not null
            ? !BlobGasCalculator.TryCalculateFeePerBlobGas(blockHeader.ExcessBlobGas.Value, spec.BlobBaseFeeUpdateFraction, out UInt256 feePerBlobGas)
                ? throw new OverflowException("Blob gas price calculation led to overflow.")
                : feePerBlobGas
            : UInt256.Zero;
}

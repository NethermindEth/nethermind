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
    public readonly ValueHash256 PrevRandao;
    public readonly bool IsGenesis;

    public BlockExecutionContext(BlockHeader blockHeader, IReleaseSpec spec)
        : this(blockHeader, spec, GetBlobBaseFee(blockHeader, spec), GetDefaultPrevRandao(blockHeader)) { }

    public BlockExecutionContext(BlockHeader blockHeader, IReleaseSpec spec, in UInt256 blobBaseFee)
        : this(blockHeader, spec, blobBaseFee, GetDefaultPrevRandao(blockHeader)) { }

    public static BlockExecutionContext WithPrevRandao(
        BlockHeader blockHeader,
        IReleaseSpec spec,
        in ValueHash256 prevRandao)
        => new(blockHeader, spec, GetBlobBaseFee(blockHeader, spec), prevRandao);

    private BlockExecutionContext(
        BlockHeader blockHeader,
        IReleaseSpec spec,
        in UInt256 blobBaseFee,
        in ValueHash256 prevRandao)
    {
        Header = blockHeader;
        Coinbase = blockHeader.GasBeneficiary ?? Address.Zero;
        Number = (ulong)blockHeader.Number;
        GasLimit = (ulong)blockHeader.GasLimit;
        BlobBaseFee = blobBaseFee.ToValueHash();
        Spec = spec;
        PrevRandao = prevRandao;
        IsGenesis = blockHeader.IsGenesis;
    }

    private static ValueHash256 GetDefaultPrevRandao(BlockHeader blockHeader) => blockHeader.IsPostMerge
        ? (blockHeader.Random ?? Hash256.Zero).ValueHash256
        : blockHeader.Difficulty.ToValueHash();

    private static UInt256 GetBlobBaseFee(BlockHeader? blockHeader, IReleaseSpec spec) =>
        blockHeader?.ExcessBlobGas is not null
            ? !BlobGasCalculator.TryCalculateFeePerBlobGas(blockHeader.ExcessBlobGas.Value, spec.BlobBaseFeeUpdateFraction, out UInt256 feePerBlobGas)
                ? throw new OverflowException("Blob gas price calculation led to overflow.")
                : feePerBlobGas
            : UInt256.Zero;
}

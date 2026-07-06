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

    /// <summary>
    /// True when the owning transaction processor is a BAL parallel-execution worker.
    /// </summary>
    /// <remarks>
    /// Parallel workers execute transactions independently against a per-tx snapshot, so they
    /// must neither accumulate <c>header.GasUsed</c> nor gate admission on cumulative block gas.
    /// Set only on the parallel <c>BlockAccessListManager</c> worker path; <c>false</c> everywhere
    /// else (default) so all existing construction sites keep sequential semantics.
    /// </remarks>
    public readonly bool Parallel;

    public BlockExecutionContext(BlockHeader blockHeader, IReleaseSpec spec)
        : this(blockHeader, spec, GetBlobBaseFee(blockHeader, spec), GetDefaultPrevRandao(blockHeader)) { }

    public BlockExecutionContext(BlockHeader blockHeader, IReleaseSpec spec, in UInt256 blobBaseFee)
        : this(blockHeader, spec, blobBaseFee, GetDefaultPrevRandao(blockHeader)) { }

    public static BlockExecutionContext WithPrevRandao(
        BlockHeader blockHeader,
        IReleaseSpec spec,
        in ValueHash256 prevRandao)
        => new(blockHeader, spec, GetBlobBaseFee(blockHeader, spec), prevRandao);

    public static BlockExecutionContext WithPrevRandaoAndBlobBaseFee(
        BlockHeader blockHeader,
        IReleaseSpec spec,
        in ValueHash256 prevRandao,
        in UInt256 blobBaseFee)
        => new(blockHeader, spec, blobBaseFee, prevRandao);

    private BlockExecutionContext(
        BlockHeader blockHeader,
        IReleaseSpec spec,
        in UInt256 blobBaseFee,
        in ValueHash256 prevRandao)
    {
        Header = blockHeader;
        Coinbase = blockHeader.GasBeneficiary ?? Address.Zero;
        Number = blockHeader.Number;
        GasLimit = blockHeader.GasLimit;
        BlobBaseFee = blobBaseFee.ToValueHash();
        Spec = spec;
        PrevRandao = prevRandao;
        IsGenesis = blockHeader.IsGenesis;
        Parallel = false;
    }

    /// <summary>Copies <paramref name="other"/> with <see cref="Parallel"/> overridden.</summary>
    public BlockExecutionContext(in BlockExecutionContext other, bool parallel)
    {
        Header = other.Header;
        Coinbase = other.Coinbase;
        Number = other.Number;
        GasLimit = other.GasLimit;
        BlobBaseFee = other.BlobBaseFee;
        Spec = other.Spec;
        PrevRandao = other.PrevRandao;
        IsGenesis = other.IsGenesis;
        Parallel = parallel;
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

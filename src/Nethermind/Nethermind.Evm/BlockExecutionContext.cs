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
    /// When set, EIP-3607 (reject transactions from senders with deployed code) is not enforced for this
    /// block's transactions. Used by eth_simulateV1 to allow state-overridden contract addresses as senders
    /// without wrapping the spec, keeping its concrete runtime type for chain-specific tx processors.
    /// </summary>
    public readonly bool SkipSenderCodeCheck;

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
        in UInt256 blobBaseFee,
        bool skipSenderCodeCheck = false)
        => new(blockHeader, spec, blobBaseFee, prevRandao, skipSenderCodeCheck);

    private BlockExecutionContext(
        BlockHeader blockHeader,
        IReleaseSpec spec,
        in UInt256 blobBaseFee,
        in ValueHash256 prevRandao,
        bool skipSenderCodeCheck = false)
    {
        Header = blockHeader;
        Coinbase = blockHeader.GasBeneficiary ?? Address.Zero;
        Number = blockHeader.Number;
        GasLimit = blockHeader.GasLimit;
        BlobBaseFee = blobBaseFee.ToValueHash();
        Spec = spec;
        PrevRandao = prevRandao;
        IsGenesis = blockHeader.IsGenesis;
        SkipSenderCodeCheck = skipSenderCodeCheck;
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

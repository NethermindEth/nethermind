// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using System.Collections.Immutable;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc;

public class XdcSubnetBlockHeader(
    Hash256 parentHash,
    Hash256 unclesHash,
    Address beneficiary,
    in UInt256 difficulty,
    ulong number,
    ulong gasLimit,
    ulong timestamp,
    byte[] extraData,
    bool isSelfMined = false) : XdcBlockHeader(parentHash, unclesHash, beneficiary, difficulty, number, gasLimit, timestamp, extraData, isSelfMined)
{
    private static readonly XdcSubnetHeaderDecoder _headerDecoder = new();

    public byte[]? NextValidators { get; set; }

    private ImmutableArray<Address>? _nextValidatorsAddress;
    public ImmutableArray<Address>? NextValidatorsAddress
    {
        get
        {
            if (_nextValidatorsAddress is not null)
                return _nextValidatorsAddress;
            _nextValidatorsAddress = XdcExtensions.ExtractAddresses(NextValidators)?.ToImmutableArray();
            return _nextValidatorsAddress;
        }
        set { _nextValidatorsAddress = value; }
    }

    public override ValueHash256 CalculateHash()
    {
        KeccakRlpWriter writer = new();
        _headerDecoder.Encode(ref writer, this);
        return writer.GetHash();
    }

    /// <inheritdoc />
    public override BlockHeader CreateSimulatedChild(ulong timestamp)
    {
        Hash256? requestsHash = RequestsHash;
        return new XdcSubnetBlockHeader(
            Hash!,
            Keccak.OfAnEmptySequenceRlp,
            Beneficiary!,
            UInt256.Zero,
            Number + 1,
            GasLimit,
            timestamp,
            [],
            IsSelfMined)
        {
            MixHash = Hash256.Zero,
            RequestsHash = requestsHash,
        };
    }

    public static new XdcSubnetBlockHeader FromBlockHeader(BlockHeader src)
    {
        XdcSubnetBlockHeader x = new(
            src.ParentHash,
            src.UnclesHash,
            src.Beneficiary,
            src.Difficulty,
            src.Number,
            src.GasLimit,
            src.Timestamp,
            src.ExtraData)
        {
            Bloom = src.Bloom ?? Bloom.Empty,
            Hash = src.Hash,
            MixHash = src.MixHash,
            Nonce = src.Nonce,
            TxRoot = src.TxRoot,
            TotalDifficulty = src.TotalDifficulty,
            AuRaStep = src.AuRaStep,
            AuRaSignature = src.AuRaSignature,
            ReceiptsRoot = src.ReceiptsRoot,
            BaseFeePerGas = src.BaseFeePerGas,
            WithdrawalsRoot = src.WithdrawalsRoot,
            RequestsHash = src.RequestsHash,
            IsPostMerge = src.IsPostMerge,
            ParentBeaconBlockRoot = src.ParentBeaconBlockRoot,
            ExcessBlobGas = src.ExcessBlobGas,
            BlobGasUsed = src.BlobGasUsed,
        };

        if (src is XdcBlockHeader xdc)
        {
            x.Validator = xdc.Validator;
            x.Validators = xdc.Validators;
            x.Penalties = xdc.Penalties;
        }

        if (src is XdcSubnetBlockHeader subnet)
        {
            x.NextValidators = subnet.NextValidators;
        }

        return x;
    }
}

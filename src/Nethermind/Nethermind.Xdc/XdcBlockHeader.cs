// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Immutable;

namespace Nethermind.Xdc;

public class XdcBlockHeader(
    Hash256 parentHash,
    Hash256 unclesHash,
    Address beneficiary,
    in UInt256 difficulty,
    ulong number,
    ulong gasLimit,
    ulong timestamp,
    byte[] extraData,
    bool isSelfMined = false
) : BlockHeader(parentHash, unclesHash, beneficiary, difficulty, number, gasLimit, timestamp, extraData), IHashResolver
{
    private static readonly XdcHeaderDecoder _headerDecoder = new();
    private static readonly ExtraConsensusDataDecoder _extraConsensusDataDecoder = new();

    public byte[]? Validators { get; set; }

    private ImmutableArray<Address>? _validatorsAddress;
    public ImmutableArray<Address>? ValidatorsAddress
    {
        get
        {
            if (_validatorsAddress is not null)
                return _validatorsAddress;
            _validatorsAddress = XdcExtensions.ExtractAddresses(Validators)?.ToImmutableArray();
            return _validatorsAddress;
        }
        set { _validatorsAddress = value; }
    }
    public byte[]? Validator { get; set; }
    public byte[]? Penalties { get; set; }

    private ImmutableArray<Address>? _penaltiesAddress;
    public ImmutableArray<Address>? PenaltiesAddress
    {
        get
        {
            if (_penaltiesAddress is not null)
                return _penaltiesAddress;
            _penaltiesAddress = XdcExtensions.ExtractAddresses(Penalties)?.ToImmutableArray();
            return _penaltiesAddress;
        }
        set { _penaltiesAddress = value; }
    }

    private ExtraFieldsV2? _extraFieldsV2;
    /// <summary>
    /// Consensus data that must be included in a V2 block, which contains the quorum certificate and round information.
    /// </summary>
    public ExtraFieldsV2? ExtraConsensusData
    {
        get
        {
            if (_extraFieldsV2 is not null)
            {
                return _extraFieldsV2;
            }

            if (ExtraData is null || ExtraData.Length == 0)
                return null;

            //Check V2 consensus version in ExtraData field.
            if (ExtraData.Length < 3 || ExtraData[0] != XdcConstants.ConsensusVersion)
                return null;
            RlpReader reader = new(ExtraData.AsSpan(1));
            _extraFieldsV2 = _extraConsensusDataDecoder.Decode(ref reader);
            return _extraFieldsV2;
        }
        internal set
        {
            _extraFieldsV2 = value;
            ExtraData = value is null ? [] : [XdcConstants.ConsensusVersion, .. _extraConsensusDataDecoder.EncodeAsBytes(value)];
        }
    }

    public bool IsSelfMined { get; } = isSelfMined;

    internal XdcProcessedRewards? ProcessedRewards { get; set; }

    public virtual ValueHash256 CalculateHash(RlpBehaviors behaviors = RlpBehaviors.None)
    {
        KeccakRlpWriter writer = new();
        _headerDecoder.Encode(ref writer, this, behaviors);
        return writer.GetHash();
    }

    /// <inheritdoc />
    public override BlockHeader CreateSimulatedChild(ulong timestamp)
    {
        Hash256? requestsHash = RequestsHash;
        return new XdcBlockHeader(
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

    internal virtual XdcBlockHeader CreateHeaderForProcessing()
    {
        XdcBlockHeader header = new(
            ParentHash,
            UnclesHash,
            Beneficiary,
            Difficulty,
            Number,
            GasLimit,
            Timestamp,
            ExtraData,
            IsSelfMined);

        CopyFieldsForProcessing(header);

        return header;
    }

    protected void CopyFieldsForProcessing(XdcBlockHeader header)
    {
        header.Bloom = Bloom.Empty;
        header.Author = Author;
        header.Hash = Hash;
        header.MixHash = MixHash;
        header.Nonce = Nonce;
        header.TxRoot = TxRoot;
        header.TotalDifficulty = TotalDifficulty;
        header.ReceiptsRoot = ReceiptsRoot;
        header.BaseFeePerGas = BaseFeePerGas;
        header.WithdrawalsRoot = WithdrawalsRoot;
        header.RequestsHash = RequestsHash;
        header.IsPostMerge = IsPostMerge;
        header.ParentBeaconBlockRoot = ParentBeaconBlockRoot;
        header.ExcessBlobGas = ExcessBlobGas;
        header.BlobGasUsed = BlobGasUsed;
        header.Validator = Validator;
        header.Validators = Validators;
        header.Penalties = Penalties;
        header.ProcessedRewards = ProcessedRewards;
    }

    public static XdcBlockHeader FromBlockHeader(BlockHeader src)
    {
        XdcBlockHeader x = new(
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
            ReceiptsRoot = src.ReceiptsRoot,
            BaseFeePerGas = src.BaseFeePerGas,
            WithdrawalsRoot = src.WithdrawalsRoot,
            RequestsHash = src.RequestsHash,
            IsPostMerge = src.IsPostMerge,
            ParentBeaconBlockRoot = src.ParentBeaconBlockRoot,
            ExcessBlobGas = src.ExcessBlobGas,
            BlobGasUsed = src.BlobGasUsed,
        };

        return x;
    }
}

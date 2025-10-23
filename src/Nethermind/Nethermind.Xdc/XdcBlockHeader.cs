// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
public class XdcBlockHeader : BlockHeader, IHashResolver
{
    private static XdcHeaderDecoder _headerDecoder = new();
    private static readonly ExtraConsensusDataDecoder _extraConsensusDataDecoder = new();
    public XdcBlockHeader(
        Hash256 parentHash,
        Hash256 unclesHash,
        Address beneficiary,
        in UInt256 difficulty,
        long number,
        long gasLimit,
        ulong timestamp,
        byte[] extraData)
        : base(parentHash, unclesHash, beneficiary, difficulty, number, gasLimit, timestamp, extraData)
    {
    }

    public byte[]? Validators { get; set; }

    private ImmutableArray<Address>? _validatorsAddress;
    public ImmutableArray<Address>? ValidatorsAddress
    {
        get
        {
            if (_validatorsAddress is not null)
                return _validatorsAddress;
            _validatorsAddress = XdcExtensions.ExtractAddresses(Validators);
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
            _penaltiesAddress = XdcExtensions.ExtractAddresses(Penalties);
            return _penaltiesAddress;
        }
        set { _penaltiesAddress = value; }
    }

    private ExtraFieldsV2 _extraFieldsV2;
    /// <summary>
    /// Consensus data that must be included in a V2 block, which contains the quorum certificate and round information.
    /// </summary>
    public ExtraFieldsV2? ExtraConsensusData
    {
        get
        {
            if (ExtraData is null || ExtraData.Length == 0)
                return null;

            if (_extraFieldsV2 == null)
            {
                //Check V2 consensus version in ExtraData field.
                if (ExtraData.Length < 3 || ExtraData[0] != XdcConstants.ConsensusVersion)
                    return null;
                Rlp.ValueDecoderContext valueDecoderContext = new Rlp.ValueDecoderContext(ExtraData.AsSpan(1));
                _extraFieldsV2 = _extraConsensusDataDecoder.Decode(ref valueDecoderContext);
            }
            return _extraFieldsV2;
        }
        set { _extraFieldsV2 = value; }
    }

    public ValueHash256 CalculateHash()
    {
        KeccakRlpStream rlpStream = new KeccakRlpStream();
        _headerDecoder.Encode(rlpStream, this);
        return rlpStream.GetHash();
    }
}

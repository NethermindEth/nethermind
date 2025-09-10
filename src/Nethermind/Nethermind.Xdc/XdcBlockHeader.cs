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
public class XdcBlockHeader : BlockHeader
{
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

    private ImmutableSortedSet<Address>? _validatorsAddress;
    public ImmutableSortedSet<Address>? ValidatorsAddress
    {
        get
        {
            if (_validatorsAddress is not null)
                return _validatorsAddress;
            _validatorsAddress = ExtractAddresses(Validators);
            return _validatorsAddress;
        }
        set { _validatorsAddress = value; }
    }
    public byte[]? Validator { get; set; }
    public byte[]? Penalties { get; set; }

    private ImmutableSortedSet<Address>? _penaltiesAddress;
    public ImmutableSortedSet<Address>? PenaltiesAddress
    {
        get
        {
            if (_penaltiesAddress is not null)
                return _penaltiesAddress;
            _penaltiesAddress = ExtractAddresses(Penalties);
            return _penaltiesAddress;
        }
        set { _penaltiesAddress = value; }
    }

    internal Address[] GetMasterNodesFromEpochSwitchHeader()
    {
        if (Validators == null)
            throw new InvalidOperationException("Header has no validators.");
        Address[] masterNodes = new Address[Validators.Length / 20];
        for (int i = 0; i < masterNodes.Length; i++)
        {
            masterNodes[i] = new Address(Validators.AsSpan(i * 20, 20));
        }
        return masterNodes;
    }

    private ExtraFieldsV2 _extraFieldsV2;
    public ExtraFieldsV2? ExtraConsensusData
    {
        get
        {
            if (ExtraData is null || ExtraData.Length == 0)
                return null;

            if (_extraFieldsV2 == null)
            {
                //Check V2 consensus version in ExtraData field.
                if (ExtraData.Length < 3 || ExtraData[0] != 2)
                    return null;
                Rlp.ValueDecoderContext valueDecoderContext = new Rlp.ValueDecoderContext(ExtraData.AsSpan(1));
                _extraFieldsV2 = _extraConsensusDataDecoder.Decode(ref valueDecoderContext);
            }
            return _extraFieldsV2;
        }
        set { _extraFieldsV2 = value; }
    }

    public bool IsEpochSwitch(ISpecProvider specProvider)
    {
        IXdcReleaseSpec spec = specProvider.GetXdcSpec(this);
        if (spec.SwitchBlock == this.Number)
        {
            return true;
        }
        ExtraFieldsV2? extraFields = ExtraConsensusData;
        throw new NotImplementedException();
    }

    private static ImmutableSortedSet<Address>? ExtractAddresses(byte[]? data)
    {
        if (data is null || data.Length % Address.Size != 0)
            return null;

        Address[] addresses = new Address[data.Length / Address.Size];
        for (int i = 0; i < addresses.Length; i++)
        {
            addresses[i] = new Address(data.AsSpan(i * Address.Size, Address.Size));
        }
        return addresses.ToImmutableSortedSet();
    }
}


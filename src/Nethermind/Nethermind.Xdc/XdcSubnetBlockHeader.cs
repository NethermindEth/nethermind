// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using System.Collections.Immutable;

namespace Nethermind.Xdc;

public class XdcSubnetBlockHeader : XdcBlockHeader
{
    private static readonly XdcSubnetHeaderDecoder _headerDecoder = new();

    public XdcSubnetBlockHeader(
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

    public byte[]? NextValidators { get; set; }

    private ImmutableArray<Address>? _nextValidatorsAddress;
    public ImmutableArray<Address>? NextValidatorsAddress
    {
        get
        {
            if (_nextValidatorsAddress is not null)
                return _nextValidatorsAddress;
            _nextValidatorsAddress = XdcExtensions.ExtractAddresses(NextValidators).ToImmutableArray();
            return _nextValidatorsAddress;
        }
        set { _nextValidatorsAddress = value; }
    }

    public override ValueHash256 CalculateHash()
    {
        KeccakRlpStream rlpStream = new KeccakRlpStream();
        _headerDecoder.Encode(rlpStream, this);
        return rlpStream.GetHash();
    }
}

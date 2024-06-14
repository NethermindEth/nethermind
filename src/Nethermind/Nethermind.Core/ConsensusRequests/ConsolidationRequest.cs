// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using System.Text;

namespace Nethermind.Core.ConsensusRequests;

/// <summary>
/// Represents a Deposit that has been validated at the consensus layer.
/// </summary>
public class ConsolidationRequest : ConsensusRequest
{
    public ConsolidationRequest()
    {
        Type = ConsensusRequestsType.ConsolidationRequest;
    }
    public Address? SourceAddress
    {
        get { return SourceAddressField; }
        set { SourceAddressField = value; }
    }

    public byte[]? SourcePubKey
    {
        get { return PubKeyField; }
        set { PubKeyField = value; }
    }

    public byte[]? TargetPubKey
    {
        get { return TargetPubKeyField; }
        set { TargetPubKeyField = value; }
    }

    public override string ToString() => ToString(string.Empty);

    public string ToString(string indentation) => @$"{indentation}{nameof(ConsolidationRequest)}
            {{ {nameof(SourceAddress)}: {SourceAddress},
            {nameof(SourcePubKey)}: {SourcePubKey?.ToHexString()},
            {nameof(TargetPubKey)}: {TargetPubKey?.ToHexString()},
            }}";


}

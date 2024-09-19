// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using System;
using Nethermind.Core.Extensions;

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
    public Address? SourceAddress { get; set; }
    public Memory<byte>? SourcePubkey { get; set; }

    public byte[]? TargetPubkey { get; set; }

    public override string ToString() => ToString(string.Empty);

    public string ToString(string indentation) => @$"{indentation}{nameof(ConsolidationRequest)}
            {{ {nameof(SourceAddress)}: {SourceAddress},
            {nameof(SourcePubkey)}: {SourcePubkey?.Span.ToHexString()},
            {nameof(TargetPubkey)}: {TargetPubkey?.ToHexString()},
            }}";


}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using System;
using System.Linq;
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

    public Memory<byte>? TargetPubkey { get; set; }

    public override string ToString() => ToString(string.Empty);

    public string ToString(string indentation) => @$"{indentation}{nameof(ConsolidationRequest)}
            {{ {nameof(SourceAddress)}: {SourceAddress},
            {nameof(SourcePubkey)}: {SourcePubkey?.Span.ToHexString()},
            {nameof(TargetPubkey)}: {TargetPubkey?.Span.ToHexString()},
            }}";

    public override byte[] Encode()
    {
        byte[] type = new byte[] { (byte)Type };
        return type
            .Concat(SourceAddress?.Bytes ?? Array.Empty<byte>())
            .Concat(SourcePubkey?.ToArray() ?? Array.Empty<byte>())
            .Concat(TargetPubkey?.ToArray() ?? Array.Empty<byte>()).ToArray();
    }

    public override ConsensusRequest Decode(byte[] data)
    {
        if (data.Length < 2)
        {
            throw new ArgumentException("Invalid data length");
        }

        Type = (ConsensusRequestsType)data[0];
        SourceAddress = new Address(data.Slice(1, Address.Size));
        SourcePubkey = data.AsMemory().Slice(1 + Address.Size);
        TargetPubkey = data.AsMemory().Slice(1 + Address.Size + SourcePubkey.Value.Length);
        return this;
    }
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using System.Text;

namespace Nethermind.Core.ConsensusRequests;

/// <summary>
/// Represents a Deposit that has been validated at the consensus layer.
/// </summary>
public class WithdrawalRequest : ConsensusRequest
{
    public WithdrawalRequest()
    {
        Type = ConsensusRequestsType.WithdrawalRequest;
        Amount = 0;
    }
    public Address? SourceAddress { get; set; }

    public Memory<byte>? ValidatorPubkey { get; set; }

    public ulong Amount { get; set; }
    public override string ToString() => ToString(string.Empty);

    public string ToString(string indentation) => @$"{indentation}{nameof(WithdrawalRequest)}
            {{{nameof(SourceAddress)}: {SourceAddress},
            {nameof(ValidatorPubkey)}: {ValidatorPubkey?.Span.ToHexString()},
            {nameof(Amount)}: {Amount}}}";


}

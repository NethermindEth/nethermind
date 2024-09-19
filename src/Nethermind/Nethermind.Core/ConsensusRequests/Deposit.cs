// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.ConsensusRequests;

/// <summary>
/// Represents a Deposit that has been validated at the consensus layer.
/// </summary>
public class Deposit : ConsensusRequest
{
    public Deposit()
    {
        Type = ConsensusRequestsType.Deposit;
        Amount = 0;
    }
    public Memory<byte>? Pubkey { get; set; }
    public byte[]? WithdrawalCredentials { get; set; }

    public ulong Amount { get; set; }

    public byte[]? Signature { get; set; }
    public ulong? Index { get; set; }
    public override string ToString() => ToString(string.Empty);

    public string ToString(string indentation) => @$"{indentation}{nameof(Deposit)}
            {{{nameof(Index)}: {Index},
            {nameof(WithdrawalCredentials)}: {WithdrawalCredentials?.ToHexString()},
            {nameof(Amount)}: {Amount},
            {nameof(Signature)}: {Signature?.ToHexString()},
            {nameof(Pubkey)}: {Pubkey?.Span.ToHexString()}}}";


}

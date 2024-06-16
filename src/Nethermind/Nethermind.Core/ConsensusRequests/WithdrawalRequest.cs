// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    }
    public Address? SourceAddress
    {
        get { return SourceAddressField; }
        set { SourceAddressField = value; }
    }

    public byte[]? ValidatorPubkey
    {
        get { return PubKeyField; }
        set { PubKeyField = value; }
    }

    public ulong Amount
    {
        get { return AmountField; }
        set { AmountField = value; }
    }
    public override string ToString() => ToString(string.Empty);

    public string ToString(string indentation) => @$"{indentation}{nameof(WithdrawalRequest)}
            {{{nameof(SourceAddress)}: {SourceAddress},
            {nameof(ValidatorPubkey)}: {ValidatorPubkey?.ToHexString()},
            {nameof(Amount)}: {Amount}}}";


}

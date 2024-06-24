// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using System.Text;

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
    public byte[]? Pubkey
    {
        get { return PubKeyField; }
        set { PubKeyField = value; }
    }

    public byte[]? WithdrawalCredentials
    {
        get { return WithdrawalCredentialsField; }
        set { WithdrawalCredentialsField = value; }
    }

    public ulong Amount
    {
        get { return AmountField; }
        set { AmountField = value; }
    }

    public byte[]? Signature
    {
        get { return SignatureField; }
        set { SignatureField = value; }
    }
    public ulong? Index
    {
        get { return IndexField; }
        set { IndexField = value; }
    }
    public override string ToString() => ToString(string.Empty);

    public string ToString(string indentation) => @$"{indentation}{nameof(Deposit)}
            {{{nameof(Index)}: {Index},
            {nameof(WithdrawalCredentials)}: {WithdrawalCredentials?.ToHexString()},
            {nameof(Amount)}: {Amount},
            {nameof(Signature)}: {Signature?.ToHexString()},
            {nameof(Pubkey)}: {Pubkey?.ToHexString()}}}";


}

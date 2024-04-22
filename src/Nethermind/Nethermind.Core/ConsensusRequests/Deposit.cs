// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using System.Text;
using Nethermind.Core.ConsensusRequests;

namespace Nethermind.Core;

/// <summary>
/// Represents a Deposit that has been validated at the consensus layer.
/// </summary>
public class Deposit : ConsensusRequest
{
    public Deposit()
    {
        Type = RequestsType.Deposit;
    }
    public byte[]? PubKey
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

    public string ToString(string indentation) => new StringBuilder($"{indentation}{nameof(Deposit)} {{")
        .Append($"{nameof(Index)}: {Index}, ")
        .Append($"{nameof(WithdrawalCredentials)}: {WithdrawalCredentials?.ToHexString()}, ")
        .Append($"{nameof(Amount)}: {Amount}, ")
        .Append($"{nameof(Signature)}: {Signature?.ToHexString()}, ")
        .Append($"{nameof(PubKey)}: {PubKey?.ToHexString()}}}")
        .ToString();


}

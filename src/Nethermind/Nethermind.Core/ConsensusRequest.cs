// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


namespace Nethermind.Core;
using Nethermind.Core.Extensions;
using System.Text;

public struct ConsensusRequest
{
    public ConsensusRequest(
        ulong amount,
        Address? sourceAddress,
        byte[]? validatorPubkey,
        byte[]? pubKey,
        byte[]? withdrawalCredentials,
        byte[]? signature,
        ulong? index)
    {
        Amount = amount;
        SourceAddress = sourceAddress;
        ValidatorPubkey = validatorPubkey;
        PubKey = pubKey;
        WithdrawalCredentials = withdrawalCredentials;
        Signature = signature;
        Index = index;
    }
    public ulong Amount;
    public Address? SourceAddress;
    public byte[]? ValidatorPubkey;
    public byte[]? PubKey { get; set; }
    public byte[]? WithdrawalCredentials { get; set; }
    public byte[]? Signature { get; set; }
    public ulong? Index { get; set; }
    public override string ToString() => ToString(string.Empty);

     public string ToString(string indentation) => new StringBuilder($"{indentation}{nameof(ConsensusRequest)} {{")
        .Append($"{nameof(Amount)}: {Amount}, ")
        .Append($"{nameof(Index)}: {Index}, ")
        .Append($"{nameof(WithdrawalCredentials)}: {WithdrawalCredentials?.ToHexString()}, ")
        .Append($"{nameof(Signature)}: {Signature?.ToHexString()}, ")
        .Append($"{nameof(PubKey)}: {PubKey?.ToHexString()}}}")
        .Append($"{nameof(SourceAddress)}: {SourceAddress}, ")
        .Append($"{nameof(ValidatorPubkey)}: {ValidatorPubkey}, ")
        .ToString();
}

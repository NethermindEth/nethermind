// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
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

    public override byte[] Encode()
    {
        byte[] type = new byte[] { (byte)Type };
        return type
            .Concat(Pubkey?.ToArray() ?? Array.Empty<byte>())
            .Concat(WithdrawalCredentials ?? Array.Empty<byte>())
            .Concat(BitConverter.GetBytes(Amount))
            .Concat(Signature ?? Array.Empty<byte>())
            .Concat(Index.HasValue ? BitConverter.GetBytes(Index.Value) : Array.Empty<byte>()).ToArray();
    }

    public override ConsensusRequest Decode(byte[] data)
    {
        if (data.Length < 2)
        {
            throw new ArgumentException("Invalid data length");
        }

        Type = (ConsensusRequestsType)data[0];
        Pubkey = data.AsMemory()[1..];
        WithdrawalCredentials = data.Slice(1 + Pubkey.Value.Length, 32);
        Amount = BitConverter.ToUInt64(data, 1 + Pubkey.Value.Length + 32);
        Signature = data.Slice(1 + Pubkey.Value.Length + 32 + sizeof(ulong));
        Index = data.Length > 1 + Pubkey.Value.Length + 32 + sizeof(ulong) + Signature!.Length ? BitConverter.ToUInt64(data, 1 + Pubkey.Value.Length + 32 + sizeof(ulong) + Signature!.Length) : null;
        return this;
    }

}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using System.Linq;

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


    public override byte[] Encode()
    {
        byte[] sourceAddress = SourceAddress?.Bytes ?? Array.Empty<byte>();
        byte[] validatorPubkey = ValidatorPubkey?.ToArray() ?? Array.Empty<byte>();
        byte[] amount = BitConverter.GetBytes(Amount);
        byte[] type = new byte[] { (byte)Type };
        return type.Concat(sourceAddress).Concat(validatorPubkey).Concat(amount).ToArray();
    }

    public override ConsensusRequest Decode(byte[] data)
    {
        if (data.Length < 2)
        {
            throw new ArgumentException("Invalid data length");
        }

        Type = (ConsensusRequestsType)data[0];
        SourceAddress = new Address(data.Slice(1, Address.Size));
        ValidatorPubkey = data.AsMemory().Slice(1 + Address.Size);
        Amount = BitConverter.ToUInt64(data, 1 + Address.Size + ValidatorPubkey.Value.Length);
        return this;
    }
}

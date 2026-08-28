// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv5.Messages;

internal readonly record struct RequestId(ulong Value, byte Length)
{
    public const int MaxLength = sizeof(ulong);

    public static RequestId From(ReadOnlySpan<byte> requestId)
    {
        if (requestId.Length > MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(requestId), requestId.Length, $"discv5 request-id length exceeds {MaxLength}.");
        }

        ulong value = 0;
        for (int i = 0; i < requestId.Length; i++)
        {
            value = (value << 8) | requestId[i];
        }

        return new RequestId(value, checked((byte)requestId.Length));
    }

    public void CopyTo(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, Length, nameof(destination));

        ulong value = Value;
        for (int i = Length - 1; i >= 0; i--)
        {
            destination[i] = (byte)value;
            value >>= 8;
        }
    }

    public int GetRlpLength()
    {
        byte firstByte = Length == 0 ? (byte)0 : (byte)(Value >> ((Length - 1) * 8));
        return Rlp.LengthOfByteString(Length, firstByte);
    }

}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Network.Enr;

internal sealed class UnknownEntry(byte[] key, byte[] rlpValue) : EnrContentEntry<byte[]>(rlpValue)
{
    public override string Key { get; } = Encoding.UTF8.GetString(key);
    internal override string SortKey { get; } = Encoding.Latin1.GetString(key);

    internal override int GetRlpLength() => Rlp.LengthOf(key) + Value.Length;

    public override void Encode(ref RlpWriter writer)
    {
        writer.Encode(key.AsSpan());
        EncodeValue(ref writer);
    }

    protected override int GetRlpLengthOfValue() => Value.Length;

    protected override void EncodeValue(ref RlpWriter writer) => writer.WriteEncoded(Value);
}

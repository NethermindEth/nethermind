// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

internal sealed class UnknownEntry(string key, byte[] rlpValue) : EnrContentEntry<byte[]>(rlpValue)
{
    public override string Key { get; } = key;

    protected override int GetRlpLengthOfValue() => Value.Length;

    protected override void EncodeValue<TWriter>(ref TWriter writer) => writer.WriteEncodedRlp(Value);
}

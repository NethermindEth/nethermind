// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

/// <summary>The UDP port of a QUIC listener for one address family.</summary>
public sealed class QuicPortEntry(int port, bool ipv6 = false) : EnrContentEntry<int>(port)
{
    public override string Key => ipv6 ? EnrContentKey.Quic6 : EnrContentKey.Quic;
    protected override int GetRlpLengthOfValue() => Rlp.LengthOf(Value);
    protected override void EncodeValue(ref RlpWriter writer) => writer.Encode(Value);
}

/// <summary>The concatenated current and optional next SHA-256 QUIC certificate hashes.</summary>
public sealed class QuicCertificateHashesEntry(ReadOnlyMemory<byte> hashes) : EnrContentEntry<ReadOnlyMemory<byte>>(hashes)
{
    public override string Key => EnrContentKey.QuicCertificateHashes;
    protected override int GetRlpLengthOfValue() => Rlp.LengthOf(Value.Span);
    protected override void EncodeValue(ref RlpWriter writer) => writer.Encode(Value.Span);
}

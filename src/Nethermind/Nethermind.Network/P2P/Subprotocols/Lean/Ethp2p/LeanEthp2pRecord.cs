// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Lean.Ethp2p;

/// <summary>The EIP-778 entry <c>leanq = [1, udp_port]</c> advertising the ethp2p binding.</summary>
public sealed class LeanqEntry(int port) : EnrContentEntry<int>(port)
{
    public override string Key => LeanEthp2pProtocol.EnrKey;

    private int ContentLength => Rlp.LengthOf(LeanEthp2pProtocol.EnrVersion) + Rlp.LengthOf(Value);

    protected override int GetRlpLengthOfValue() => Rlp.LengthOfSequence(ContentLength);

    protected override void EncodeValue<TWriter>(ref TWriter writer)
    {
        writer.StartSequence(ContentLength);
        writer.Encode(LeanEthp2pProtocol.EnrVersion);
        writer.Encode(Value);
    }
}

/// <summary>A peer's ethp2p endpoint and node key, taken from its signed node record.</summary>
public sealed record LeanEthp2pRecord(PublicKey NodeKey, IPEndPoint EndPoint)
{
    /// <summary>Parses a signed <c>enr:</c> record; fails without <c>leanq</c> version 1, a key or an address.</summary>
    /// <remarks>EIP-8437: support is never inferred from the discovery UDP port, and unknown versions are ignored.</remarks>
    public static bool TryParse(string enr, [NotNullWhen(true)] out LeanEthp2pRecord? record, [NotNullWhen(false)] out string? error)
    {
        record = null;
        NodeRecord nodeRecord;
        try
        {
            // Verifies the record's signature.
            nodeRecord = NodeRecord.FromEnrString(enr.Trim());
        }
        catch (Exception exception) when (exception is RlpException or ArgumentException or FormatException or InvalidOperationException)
        {
            error = $"invalid ENR: {exception.Message}";
            return false;
        }
        return TryParse(nodeRecord, out record, out error);
    }

    public static bool TryParse(NodeRecord nodeRecord, [NotNullWhen(true)] out LeanEthp2pRecord? record, [NotNullWhen(false)] out string? error)
    {
        record = null;
        if (nodeRecord.GetObj<byte[]>(LeanEthp2pProtocol.EnrKey) is not { } leanq)
        {
            error = "ENR does not advertise leanq";
            return false;
        }
        if (!TryReadLeanq(leanq, out int port))
        {
            error = "ENR leanq is malformed or of an unknown version";
            return false;
        }
        if (nodeRecord.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1) is not { } key)
        {
            error = "ENR has no secp256k1 key";
            return false;
        }
        if ((nodeRecord.GetObj<IPAddress>(EnrContentKey.Ip) ?? nodeRecord.GetObj<IPAddress>(EnrContentKey.Ip6)) is not { } address)
        {
            error = "ENR has no ip or ip6 address";
            return false;
        }
        record = new LeanEthp2pRecord(key.Decompress(), new IPEndPoint(address, port));
        error = null;
        return true;
    }

    private static bool TryReadLeanq(byte[] value, out int port)
    {
        port = 0;
        try
        {
            LeanRlpReader outer = new(value);
            LeanRlpReader fields = outer.ReadList();
            outer.End();
            if (fields.ReadUInt64() != LeanEthp2pProtocol.EnrVersion) return false;
            ulong advertised = fields.ReadUInt64();
            // Trailing members are tolerated, as for other list-valued ENR entries.
            if (advertised is 0 or > ushort.MaxValue) return false;
            port = (int)advertised;
            return true;
        }
        catch (RlpException)
        {
            return false;
        }
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;

namespace Nethermind.BeaconChain.P2P.ReqResp.Protocols;

/// <summary>A numeric control request with a separate identity for each channel attempt.</summary>
public sealed record Uint64Request(ulong Value);

/// <summary>The eth2 <c>ping</c> protocol: both sides exchange their <c>MetaData.seq_number</c>.</summary>
/// <remarks>Named to avoid clashing with the libp2p <c>/ipfs/ping/1.0.0</c> protocol class.</remarks>
public sealed class Eth2PingProtocol(LocalMetadataSource metadataSource) : SingleChunkProtocol<Uint64Request, ulong>
{
    public override string Id => "/eth2/beacon_chain/req/ping/1/ssz_snappy";
    protected override int MaxRequestSize => sizeof(ulong);
    protected override int MaxResponseSize => sizeof(ulong);
    protected override byte[] EncodeRequest(Uint64Request request) => EncodeUint64(request.Value);
    protected override Uint64Request DecodeRequest(byte[] ssz) => new(DecodeUint64(ssz));
    protected override byte[] EncodeResponse(ulong response) => EncodeUint64(response);
    protected override ulong DecodeResponse(byte[] ssz) => DecodeUint64(ssz);
    protected override ulong HandleRequest(Uint64Request request) => metadataSource.Current.SeqNumber;

    internal static byte[] EncodeUint64(ulong value)
    {
        byte[] ssz = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(ssz, value);
        return ssz;
    }

    internal static ulong DecodeUint64(ReadOnlySpan<byte> ssz) => ssz.Length == sizeof(ulong)
        ? BinaryPrimitives.ReadUInt64LittleEndian(ssz)
        : throw new Eth2ReqRespException($"uint64 payload must be 8 bytes, got {ssz.Length}");
}

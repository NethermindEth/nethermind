// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.P2P.ReqResp.Protocols;

/// <summary>The Altair eth2 <c>metadata</c> v2 protocol, without a custody group count.</summary>
public sealed class MetaDataProtocolV2(LocalMetadataSource metadataSource) : SingleChunkProtocol<Uint64Request, MetaDataV2>
{
    private const int MetaDataV2Length = 17;

    public override string Id => "/eth2/beacon_chain/req/metadata/2/ssz_snappy";
    protected override int MaxRequestSize => 0;
    protected override int MaxResponseSize => MetaDataV2Length;
    protected override byte[] EncodeRequest(Uint64Request request) => [];
    protected override Uint64Request DecodeRequest(byte[] ssz) => new(0);
    protected override byte[] EncodeResponse(MetaDataV2 response) => MetaDataV2.Encode(response);
    protected override MetaDataV2 HandleRequest(Uint64Request request)
    {
        MetaDataV3 metadata = metadataSource.Current;
        return new() { SeqNumber = metadata.SeqNumber, Attnets = metadata.Attnets, Syncnets = metadata.Syncnets };
    }

    protected override MetaDataV2 DecodeResponse(byte[] ssz)
    {
        MetaDataV2.Decode(ssz, out MetaDataV2 metadata);
        return metadata;
    }
}

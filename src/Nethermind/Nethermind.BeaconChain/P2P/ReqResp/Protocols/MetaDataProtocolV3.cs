// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.P2P.ReqResp.Protocols;

/// <summary>The Fulu eth2 <c>metadata</c> v3 protocol. The request has no body; the response is our <see cref="MetaDataV3"/>.</summary>
public sealed class MetaDataProtocolV3(LocalMetadataSource metadataSource) : SingleChunkProtocol<Uint64Request, MetaDataV3>
{
    private const int MetaDataV3Length = 25;

    public override string Id => "/eth2/beacon_chain/req/metadata/3/ssz_snappy";
    protected override int MaxRequestSize => 0;
    protected override int MaxResponseSize => MetaDataV3Length;
    protected override byte[] EncodeRequest(Uint64Request request) => [];
    protected override Uint64Request DecodeRequest(byte[] ssz) => new(0);
    protected override byte[] EncodeResponse(MetaDataV3 response) => MetaDataV3.Encode(response);
    protected override MetaDataV3 HandleRequest(Uint64Request request) => metadataSource.Current;

    protected override MetaDataV3 DecodeResponse(byte[] ssz)
    {
        MetaDataV3.Decode(ssz, out MetaDataV3 metadata);
        return metadata;
    }
}

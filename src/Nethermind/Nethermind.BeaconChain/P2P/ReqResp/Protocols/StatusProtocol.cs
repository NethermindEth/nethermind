// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.P2P.ReqResp.Protocols;

/// <summary>The eth2 <c>status</c> v1 protocol; speaks <see cref="StatusMessageV2"/> at the API surface and drops <c>earliest_available_slot</c> on the wire.</summary>
public sealed class StatusProtocolV1(IBeaconChainStatusSource statusSource) : SingleChunkProtocol<StatusMessageV2, StatusMessageV2>
{
    private const int StatusV1Length = 84;

    public override string Id => "/eth2/beacon_chain/req/status/1/ssz_snappy";

    protected override int MaxRequestSize => StatusV1Length;
    protected override int MaxResponseSize => StatusV1Length;

    protected override byte[] EncodeRequest(StatusMessageV2 request) => StatusMessageV1.Encode(ToV1(request));
    protected override StatusMessageV2 DecodeRequest(byte[] ssz) => Decode(ssz);
    protected override byte[] EncodeResponse(StatusMessageV2 response) => StatusMessageV1.Encode(ToV1(response));
    protected override StatusMessageV2 DecodeResponse(byte[] ssz) => Decode(ssz);
    protected override StatusMessageV2 HandleRequest(StatusMessageV2 request) => statusSource.CurrentStatus;

    private static StatusMessageV2 Decode(byte[] ssz)
    {
        if (ssz.Length != StatusV1Length)
        {
            throw new Eth2ReqRespException($"Status v1 must be {StatusV1Length} bytes, got {ssz.Length}");
        }

        StatusMessageV1.Decode(ssz, out StatusMessageV1 status);
        return new StatusMessageV2
        {
            ForkDigest = status.ForkDigest,
            FinalizedRoot = status.FinalizedRoot,
            FinalizedEpoch = status.FinalizedEpoch,
            HeadRoot = status.HeadRoot,
            HeadSlot = status.HeadSlot,
        };
    }

    private static StatusMessageV1 ToV1(StatusMessageV2 status) => new()
    {
        ForkDigest = status.ForkDigest,
        FinalizedRoot = status.FinalizedRoot,
        FinalizedEpoch = status.FinalizedEpoch,
        HeadRoot = status.HeadRoot,
        HeadSlot = status.HeadSlot,
    };
}

/// <summary>The Fulu eth2 <c>status</c> v2 protocol carrying <c>earliest_available_slot</c>.</summary>
public sealed class StatusProtocolV2(IBeaconChainStatusSource statusSource) : SingleChunkProtocol<StatusMessageV2, StatusMessageV2>
{
    private const int StatusV2Length = 92;

    public override string Id => "/eth2/beacon_chain/req/status/2/ssz_snappy";

    protected override int MaxRequestSize => StatusV2Length;
    protected override int MaxResponseSize => StatusV2Length;

    protected override byte[] EncodeRequest(StatusMessageV2 request) => StatusMessageV2.Encode(request);
    protected override StatusMessageV2 DecodeRequest(byte[] ssz) => Decode(ssz);
    protected override byte[] EncodeResponse(StatusMessageV2 response) => StatusMessageV2.Encode(response);
    protected override StatusMessageV2 DecodeResponse(byte[] ssz) => Decode(ssz);
    protected override StatusMessageV2 HandleRequest(StatusMessageV2 request) => statusSource.CurrentStatus;

    private static StatusMessageV2 Decode(byte[] ssz)
    {
        if (ssz.Length != StatusV2Length)
        {
            throw new Eth2ReqRespException($"Status v2 must be {StatusV2Length} bytes, got {ssz.Length}");
        }

        StatusMessageV2.Decode(ssz, out StatusMessageV2 status);
        return status;
    }
}

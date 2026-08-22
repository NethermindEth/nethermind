// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.SszRest;

namespace Nethermind.Optimism.CL.P2P;

public class PayloadDecoder : IPayloadDecoder
{
    public static readonly PayloadDecoder Instance = new();

    private PayloadDecoder()
    {
    }

    /// <inheritdoc/>
    public ExecutionPayloadV3 DecodePayload(ReadOnlySpan<byte> data)
    {
        if (data.Length < Hash256.Size)
        {
            throw new InvalidDataException($"Payload is shorter than the {Hash256.Size}-byte parent beacon block root");
        }

        // The SSZ ExecutionPayload starts immediately after the parent_beacon_block_root prefix
        SszExecutionPayloadV3.Decode(data[Hash256.Size..], out SszExecutionPayloadV3 ssz);
        ExecutionPayloadV3 payload = ssz.AsExecutionPayload();
        payload.ParentBeaconBlockRoot = new Hash256(data[..Hash256.Size]);
        return payload;
    }

    public byte[] EncodePayload(ExecutionPayloadV3 payload)
    {
        ArgumentNullException.ThrowIfNull(payload.ParentBeaconBlockRoot);

        SszExecutionPayloadV3 ssz = new(payload);
        byte[] encoded = new byte[Hash256.Size + SszExecutionPayloadV3.GetLength(ssz)];
        payload.ParentBeaconBlockRoot.Bytes.CopyTo(encoded);
        SszExecutionPayloadV3.Encode(encoded.AsSpan(Hash256.Size), ssz);
        return encoded;
    }
}

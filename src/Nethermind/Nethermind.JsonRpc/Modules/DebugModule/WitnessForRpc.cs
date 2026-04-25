// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.JsonRpc.Modules.DebugModule;

public class WitnessForRpc(Witness witness)
{
    private static readonly IRlpValueDecoder<BlockHeader> _headerDecoder =
        Rlp.GetValueDecoder<BlockHeader>() ?? new HeaderDecoder();

    public IReadOnlyList<WitnessHeaderForRpc> Headers { get; } = DecodeHeaders(witness.Headers);
    public IReadOnlyDictionary<string, byte[]> Codes { get; } = BuildHashMap(witness.Codes);
    public IReadOnlyDictionary<string, byte[]> State { get; } = BuildHashMap(witness.State);

    private static IReadOnlyList<WitnessHeaderForRpc> DecodeHeaders(IOwnedReadOnlyList<byte[]> encodedHeaders)
    {
        WitnessHeaderForRpc[] headers = new WitnessHeaderForRpc[encodedHeaders.Count];
        for (int i = 0; i < encodedHeaders.Count; i++)
        {
            Rlp.ValueDecoderContext ctx = new(encodedHeaders[i]);
            BlockHeader header = _headerDecoder.Decode(ref ctx)
                ?? throw new InvalidOperationException($"Cannot decode witness header at index {i}");
            headers[i] = new WitnessHeaderForRpc(header);
        }
        return headers;
    }

    private static Dictionary<string, byte[]> BuildHashMap(IOwnedReadOnlyList<byte[]> items)
    {
        Dictionary<string, byte[]> map = new(items.Count);
        foreach (byte[] item in items)
            map[Keccak.Compute(item).ToString()] = item;
        return map;
    }
}

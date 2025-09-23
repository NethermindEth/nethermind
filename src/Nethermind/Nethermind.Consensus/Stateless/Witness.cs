// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Stateless;

public struct Witness
{
    [JsonPropertyName("codes")]
    public byte[][] Codes;
    [JsonPropertyName("state")]
    public byte[][] State;
    [JsonPropertyName("keys")]
    public byte[][] Keys;
    [JsonPropertyName("headers")]
    public byte[][] Headers;

    [JsonIgnore]
    public IReadOnlyCollection<BlockHeader> DecodedHeaders => _decodedHeaders ??= DecodeHeaders();

    private IReadOnlyCollection<BlockHeader> DecodeHeaders()
    {
        List<BlockHeader> headers = new(Headers.Length);
        HeaderDecoder decoder = new();
        foreach (var encodedHeader in Headers)
        {
            Rlp.ValueDecoderContext stream = new(encodedHeader);
            headers.Add(decoder.Decode(ref stream) ?? throw new ArgumentException());
        }
        return headers;
    }

    [JsonIgnore]
    private IReadOnlyCollection<BlockHeader>? _decodedHeaders;
}

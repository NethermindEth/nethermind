// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

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

    [JsonIgnore]
    public INodeStorage NodeStorage => _nodeStorage ??= CreateNodeStorage();

    [JsonIgnore]
    public IKeyValueStoreWithBatching CodeDb => _codeDb ??= CreateCodeDb();

    private INodeStorage CreateNodeStorage()
    {
        IKeyValueStore db = new MemDb();
        foreach (var stateElement in State)
        {
            var hash = ValueKeccak.Compute(stateElement).Bytes;
            db.PutSpan(hash, stateElement);
        }

        return new NodeStorage(db, INodeStorage.KeyScheme.Hash);
    }

    private IKeyValueStoreWithBatching CreateCodeDb()
    {
        IKeyValueStoreWithBatching db = new MemDb();
        foreach (var code in Codes)
        {
            var hash = ValueKeccak.Compute(code).Bytes;
            db.PutSpan(hash, code);
        }
        return db;
    }

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

    [JsonIgnore]
    private INodeStorage? _nodeStorage;

    [JsonIgnore]
    private IKeyValueStoreWithBatching _codeDb;
}

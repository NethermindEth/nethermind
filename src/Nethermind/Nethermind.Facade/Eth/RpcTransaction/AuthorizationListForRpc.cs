// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using static Nethermind.Facade.Eth.RpcTransaction.AuthorizationListForRpc;

namespace Nethermind.Facade.Eth.RpcTransaction;

[JsonConverter(typeof(JsonConverter))]
public class AuthorizationListForRpc : IEnumerable<RpcAuthTuple>
{
    private readonly IEnumerable<RpcAuthTuple> _tuples;

    [JsonConstructor]
    public AuthorizationListForRpc() { }

    private AuthorizationListForRpc(IEnumerable<RpcAuthTuple> tuples)
    {
        _tuples = tuples;
    }

    public class RpcAuthTuple
    {
        public UInt256 ChainId { get; set; }
        public ulong Nonce { get; set; }
        public Address Address { get; set; }
        public ulong YParity { get; set; }
        public UInt256 S { get; set; }
        public UInt256 R { get; set; }

        [JsonConstructor]
        public RpcAuthTuple() { }

        public RpcAuthTuple(UInt256 chainId, ulong nonce, Address address, ulong yParity, UInt256 s, UInt256 r)
        {
            ChainId = chainId;
            Nonce = nonce;
            Address = address;
            YParity = yParity;
            S = s;
            R = r;
        }
    }

    public static AuthorizationListForRpc FromAuthorizationList(IEnumerable<AuthorizationTuple>? authorizationList) =>
        authorizationList is null
        ? new AuthorizationListForRpc([])
        : new AuthorizationListForRpc(authorizationList.Select(static tuple =>
            new RpcAuthTuple(tuple.ChainId,
                    tuple.Nonce,
                    tuple.CodeAddress,
                    tuple.AuthoritySignature.V - Signature.VOffset,
                    new UInt256(tuple.AuthoritySignature.S.Span, true),
                    new UInt256(tuple.AuthoritySignature.R.Span, true))));

    public AuthorizationTuple[] ToAuthorizationList() => _tuples
        .Select(static tuple => new AuthorizationTuple(
            (ulong)tuple.ChainId,
            tuple.Address,
            tuple.Nonce,
            new Signature(tuple.R, tuple.S, (ulong)tuple.YParity + Signature.VOffset))
        ).ToArray();

    public IEnumerator<RpcAuthTuple> GetEnumerator()
    {
        return _tuples.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    private class JsonConverter : JsonConverter<AuthorizationListForRpc>
    {
        public override AuthorizationListForRpc? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            List<RpcAuthTuple>? list = JsonSerializer.Deserialize<List<RpcAuthTuple>>(ref reader, options);
            return list is null ? null : new AuthorizationListForRpc(list);
        }

        public override void Write(Utf8JsonWriter writer, AuthorizationListForRpc value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value._tuples, options);
        }
    }
}

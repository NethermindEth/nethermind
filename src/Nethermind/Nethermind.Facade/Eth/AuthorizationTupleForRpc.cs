// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Nethermind.Facade.Eth
{
    public struct AuthorizationTupleForRpc
    {
        [JsonConstructor]
        public AuthorizationTupleForRpc()
        {
        }
        public AuthorizationTupleForRpc(UInt256 chainId, ulong nonce, Address address, UInt256? yParity, UInt256? s, UInt256? r)
        {
            ChainId = chainId;
            Nonce = nonce;
            Address = address;
            YParity = yParity;
            S = s;
            R = r;
        }

        public UInt256 ChainId { get; set; }
        public ulong Nonce { get; set; }
        public Address Address { get; set; }
        public UInt256? YParity { get; set; }
        public UInt256? S { get; set; }
        public UInt256? R { get; set; }

        public static IEnumerable<AuthorizationTupleForRpc> FromAuthorizationList(AuthorizationTuple[] authorizationList) =>
            authorizationList.Select(tuple => new AuthorizationTupleForRpc(tuple.ChainId,
                                                                           tuple.Nonce,
                                                                           tuple.CodeAddress,
                                                                           tuple.AuthoritySignature.RecoveryId,
                                                                           new UInt256(tuple.AuthoritySignature.S),
                                                                           new UInt256(tuple.AuthoritySignature.R)));
    }
}

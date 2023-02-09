// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Les
{
    public class CodeRequest
    {
        public Keccak BlockHash;
        public Keccak AccountKey;

        public CodeRequest()
        {
        }

        public CodeRequest(Keccak blockHash, Keccak accountKey)
        {
            BlockHash = blockHash;
            AccountKey = accountKey;
        }
    }
}

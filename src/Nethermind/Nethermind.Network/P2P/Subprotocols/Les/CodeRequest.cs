// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Les
{
    public class CodeRequest
    {
        public Hash256 BlockHash;
        public Hash256 AccountKey;

        public CodeRequest()
        {
        }

        public CodeRequest(Hash256 blockHash, Hash256 accountKey)
        {
            BlockHash = blockHash;
            AccountKey = accountKey;
        }
    }
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Les
{
    public class CodeRequest
    {
        public Commitment BlockHash;
        public Commitment AccountKey;

        public CodeRequest()
        {
        }

        public CodeRequest(Commitment blockHash, Commitment accountKey)
        {
            BlockHash = blockHash;
            AccountKey = accountKey;
        }
    }
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages
{
    public class NewPooledTransactionHashesMessageSerializer
        : HashesMessageSerializer<NewPooledTransactionHashesMessage>
    {
        public override NewPooledTransactionHashesMessage Deserialize(IByteBuffer byteBuffer)
        {
            Hash256[] hashes = DeserializeHashes(byteBuffer);
            return new NewPooledTransactionHashesMessage(hashes);
        }
    }
}

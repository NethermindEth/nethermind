// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Xdc.P2P.Messages;

public class XdcNewPooledTransactionHashesMessageSerializer : HashesMessageSerializer<XdcNewPooledTransactionHashesMessage>
{
    private static readonly RlpLimit RlpLimit = RlpLimit.For<XdcNewPooledTransactionHashesMessage>(NethermindSyncLimits.MaxHashesFetch, nameof(XdcNewPooledTransactionHashesMessage.Hashes));

    public override XdcNewPooledTransactionHashesMessage Deserialize(IByteBuffer byteBuffer)
    {
        ArrayPoolList<Hash256> hashes = DeserializeHashesArrayPool(byteBuffer, RlpLimit);
        return new XdcNewPooledTransactionHashesMessage(hashes);
    }
}

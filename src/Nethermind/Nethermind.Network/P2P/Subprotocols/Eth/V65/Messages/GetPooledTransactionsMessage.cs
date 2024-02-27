// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages
{
    public class GetPooledTransactionsMessage(IOwnedReadOnlyList<Hash256> hashes) : HashesMessage(hashes)
    {
        public override int PacketType { get; } = Eth65MessageCode.GetPooledTransactions;
        public override string Protocol { get; } = "eth";

        public override string ToString() => $"{nameof(GetPooledTransactionsMessage)}({Hashes?.Count})";
    }
}

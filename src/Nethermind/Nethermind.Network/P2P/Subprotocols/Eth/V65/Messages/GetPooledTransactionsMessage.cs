// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;

public class GetPooledTransactionsMessage(IOwnedReadOnlyList<Hash256> hashes) : HashesMessage(hashes), INew<IOwnedReadOnlyList<Hash256>, GetPooledTransactionsMessage>
{
    public override int PacketType => Eth65MessageCode.GetPooledTransactions;
    public override string Protocol => "eth";

    public static GetPooledTransactionsMessage New(IOwnedReadOnlyList<Hash256> arg) => new(arg);

    public override string ToString() => $"{nameof(GetPooledTransactionsMessage)}({Hashes?.Count})";
}

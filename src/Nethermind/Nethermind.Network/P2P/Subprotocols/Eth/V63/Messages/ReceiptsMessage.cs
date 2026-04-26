// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages
{
    public class ReceiptsMessage(IOwnedReadOnlyList<TxReceipt[]> txReceipts) : P2PMessage
    {
        public IOwnedReadOnlyList<TxReceipt[]?> TxReceipts { get; } = txReceipts ?? ArrayPoolList<TxReceipt[]>.Empty();
        public override int PacketType => Eth63MessageCode.Receipts;
        public override string Protocol => "eth";

        private static ReceiptsMessage? _empty;
        public static ReceiptsMessage Empty => _empty ??= new ReceiptsMessage(null);

        public override string ToString() => $"{nameof(ReceiptsMessage)}({TxReceipts?.Count ?? 0})";

        public override void Dispose()
        {
            base.Dispose();
            TxReceipts?.Dispose();
        }
    }
}

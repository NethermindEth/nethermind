// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages
{
    public class ReceiptsMessage : P2PMessage
    {
        public TxReceipt[][] TxReceipts { get; }
        public override int PacketType { get; } = Eth63MessageCode.Receipts;
        public override string Protocol { get; } = "eth";

        private static ReceiptsMessage? _empty;
        public static ReceiptsMessage Empty => _empty ??= new ReceiptsMessage(null);

        public ReceiptsMessage(TxReceipt[][] txReceipts)
        {
            TxReceipts = txReceipts ?? new TxReceipt[0][];
        }

        public override string ToString() => $"{nameof(ReceiptsMessage)}({TxReceipts?.Length ?? 0})";
    }
}

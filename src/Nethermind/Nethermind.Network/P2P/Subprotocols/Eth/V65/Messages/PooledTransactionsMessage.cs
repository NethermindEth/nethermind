// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages
{
    public class PooledTransactionsMessage(IOwnedReadOnlyList<Transaction> transactions)
        : TransactionsMessage(transactions)
    {
        public override int PacketType => Eth65MessageCode.PooledTransactions;
        public override string Protocol => "eth";

        /// <summary>Transactions that the response carries as encodings, which this message owns.</summary>
        /// <remarks>
        /// They go into the response together with <see cref="TransactionsMessage.Transactions"/>, at the places that
        /// <see cref="EncodedTransaction.TransactionIndex"/> gives.
        /// </remarks>
        internal IOwnedReadOnlyList<EncodedTransaction>? EncodedTransactions { get; set; }

        public override string ToString() => $"{nameof(PooledTransactionsMessage)}({Transactions?.Count})";

        public override void Dispose()
        {
            base.Dispose();
            DisposeEncodedTransactions(EncodedTransactions);
            EncodedTransactions = null;
        }

        internal static void DisposeEncodedTransactions(IOwnedReadOnlyList<EncodedTransaction>? encodedTransactions)
        {
            if (encodedTransactions is null) return;

            foreach (EncodedTransaction encodedTransaction in encodedTransactions.AsSpan())
            {
                encodedTransaction.Encoding.Dispose();
            }

            encodedTransactions.Dispose();
        }

        /// <summary>A transaction of the response as its network encoding.</summary>
        /// <param name="TransactionIndex">The number of <see cref="TransactionsMessage.Transactions"/> that go before it.</param>
        /// <param name="Encoding">The mempool encoding without the typed envelope.</param>
        internal readonly record struct EncodedTransaction(int TransactionIndex, IOwnedReadOnlyList<byte> Encoding);
    }
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.TxPool
{
    public class TxPoolSender : ITxSender
    {
        private readonly ITxPool _txPool;
        private readonly ITxSealer _sealer;

        public TxPoolSender(ITxPool txPool, ITxSealer sealer)
        {
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _sealer = sealer ?? throw new ArgumentNullException(nameof(sealer));
        }

        public ValueTask<(Keccak, AcceptTxResult?)> SendTransaction(Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            _sealer.Seal(tx, txHandlingOptions);
            AcceptTxResult result = _txPool.SubmitTx(tx, txHandlingOptions);
            return new ValueTask<(Keccak, AcceptTxResult?)>((tx.Hash!, result)); // The sealer calculates the hash
        }
    }
}

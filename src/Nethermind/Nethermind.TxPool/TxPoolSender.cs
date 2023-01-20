// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;

namespace Nethermind.TxPool
{
    public class TxPoolSender : ITxSender
    {
        private readonly ITxPool _txPool;
        private readonly ITxSealer _sealer;
        private readonly INonceManager _nonceManager;
        private readonly IEthereumEcdsa _ecdsa;

        public TxPoolSender(ITxPool txPool, ITxSealer sealer, INonceManager nonceManager, IEthereumEcdsa ecdsa)
        {
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _sealer = sealer ?? throw new ArgumentNullException(nameof(sealer));
            _nonceManager = nonceManager ?? throw new ArgumentNullException(nameof(nonceManager));
            _ecdsa = ecdsa ?? throw new ArgumentException(nameof(ecdsa));
        }

        public ValueTask<(Keccak, AcceptTxResult?)> SendTransaction(Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            bool manageNonce = (txHandlingOptions & TxHandlingOptions.ManagedNonce) == TxHandlingOptions.ManagedNonce;
            tx.SenderAddress ??= _ecdsa.RecoverAddress(tx);
            if (tx.SenderAddress is null)
                throw new ArgumentNullException(nameof(tx.SenderAddress));

            AcceptTxResult result = manageNonce
                ? SubmitTxWithManagedNonce(tx, txHandlingOptions)
                : SubmitTxWithNonce(tx, txHandlingOptions);

            return new ValueTask<(Keccak, AcceptTxResult?)>((tx.Hash!, result)); // The sealer calculates the hash
        }

        private AcceptTxResult SubmitTxWithManagedNonce(Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            using IDisposable locker = _nonceManager.TxWithNonceReceived(tx.SenderAddress!, tx.Nonce);

            _sealer.Seal(tx, txHandlingOptions);
            AcceptTxResult result = _txPool.SubmitTx(tx, txHandlingOptions);


            if (result == AcceptTxResult.Accepted)
            {
                _nonceManager.TxAccepted(tx.SenderAddress!);
            }

            return result;
        }

        private AcceptTxResult SubmitTxWithNonce(Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            using IDisposable locker = _nonceManager.ReserveNonce(tx.SenderAddress!, out UInt256 nonce);
            txHandlingOptions |= TxHandlingOptions.AllowReplacingSignature;
            tx.Nonce = nonce;

            return SubmitTx(tx, txHandlingOptions);
        }

        private AcceptTxResult SubmitTx(Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            _sealer.Seal(tx, txHandlingOptions);
            AcceptTxResult result = _txPool.SubmitTx(tx, txHandlingOptions);


            if (result == AcceptTxResult.Accepted)
            {
                _nonceManager.TxAccepted(tx.SenderAddress!);
            }

            return result;
        }
    }
}

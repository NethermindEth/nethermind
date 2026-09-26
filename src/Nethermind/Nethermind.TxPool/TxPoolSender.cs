// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.TxPool
{
    public class TxPoolSender(ITxPool txPool, ITxSealer sealer, INonceManager nonceManager, IEthereumEcdsa ecdsa) : ITxSender
    {
        private readonly ITxPool _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
        private readonly ITxSealer _sealer = sealer ?? throw new ArgumentNullException(nameof(sealer));
        private readonly INonceManager _nonceManager = nonceManager ?? throw new ArgumentNullException(nameof(nonceManager));
        private readonly IEthereumEcdsa _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));

        public ValueTask<(Hash256, AcceptTxResult?)> SendTransaction(Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            bool manageNonce = (txHandlingOptions & TxHandlingOptions.ManagedNonce) == TxHandlingOptions.ManagedNonce;

            if (tx.SenderAddress is null)
            {
                if (!_ecdsa.TryRecoverAddress(tx, out Address? senderAddress))
                {
                    return new((tx.Hash!, AcceptTxResult.FailedToResolveSender));
                }
                tx.SenderAddress = senderAddress;
            }

            // An EIP-8250 keyed sequence lives in NONCE_MANAGER, not the sender's account nonce: there is nothing to
            // reserve, and recording one would push the managed reservation past a nonce the sender never consumes.
            AcceptTxResult result = KeyedNonceManager.UsesKeyedNonce(tx)
                ? SubmitTx(tx, txHandlingOptions)
                : manageNonce
                    ? SubmitTxWithManagedNonce(tx, txHandlingOptions)
                    : SubmitTxWithNonce(tx, txHandlingOptions);

            return new ValueTask<(Hash256, AcceptTxResult?)>((tx.Hash!, result)); // The sealer calculates the hash
        }

        private AcceptTxResult SubmitTxWithNonce(Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            using NonceLocker locker = _nonceManager.TxWithNonceReceived(tx.SenderAddress!, tx.Nonce);
            return SubmitTx(locker, tx, txHandlingOptions);
        }

        private AcceptTxResult SubmitTxWithManagedNonce(Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            using NonceLocker locker = _nonceManager.ReserveNonce(tx.SenderAddress!, out ulong reservedNonce);
            txHandlingOptions |= TxHandlingOptions.AllowReplacingSignature;
            tx.Nonce = reservedNonce;
            return SubmitTx(locker, tx, txHandlingOptions);
        }

        private AcceptTxResult SubmitTx(NonceLocker locker, Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            AcceptTxResult result = SubmitTx(tx, txHandlingOptions);

            if (result == AcceptTxResult.Accepted)
            {
                locker.Accept();
            }

            return result;
        }

        private AcceptTxResult SubmitTx(Transaction tx, TxHandlingOptions txHandlingOptions) =>
            _sealer.TrySeal(tx, txHandlingOptions)
                ? _txPool.SubmitTx(tx, txHandlingOptions)
                : AcceptTxResult.SignFailed;
    }
}

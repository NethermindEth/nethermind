// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.TxPool
{
    public class TxPoolSender(ITxPool txPool, ITxSealer sealer, INonceManager nonceManager, IEthereumEcdsa ecdsa, IChainHeadSpecProvider specProvider) : ITxSender
    {
        private static readonly AcceptTxResult SenderIsNotSigner = AcceptTxResult.Invalid.WithMessage("sender is not the signer");

        private readonly ITxPool _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
        private readonly ITxSealer _sealer = sealer ?? throw new ArgumentNullException(nameof(sealer));
        private readonly INonceManager _nonceManager = nonceManager ?? throw new ArgumentNullException(nameof(nonceManager));
        private readonly IEthereumEcdsa _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
        private readonly IChainHeadSpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));

        public ValueTask<(Hash256, AcceptTxResult?)> SendTransaction(Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            bool manageNonce = (txHandlingOptions & TxHandlingOptions.ManagedNonce) == TxHandlingOptions.ManagedNonce;

            // A sender recovered here is the signer; a sender the caller set is checked once the transaction is sealed.
            bool senderGiven = tx.SenderAddress is not null;
            if (!senderGiven)
            {
                if (!_ecdsa.TryRecoverAddress(tx, out Address? senderAddress, UsesSignatureChainId()))
                {
                    return new((tx.Hash!, AcceptTxResult.FailedToResolveSender));
                }
                tx.SenderAddress = senderAddress;
            }

            // An EIP-8250 keyed sequence lives in NONCE_MANAGER, not the sender's account nonce: there is nothing to
            // reserve, and recording one would push the managed reservation past a nonce the sender never consumes.
            AcceptTxResult result = KeyedNonceManager.UsesKeyedNonce(tx)
                ? SubmitTx(tx, txHandlingOptions, senderGiven)
                : manageNonce
                    ? SubmitTxWithManagedNonce(tx, txHandlingOptions, senderGiven)
                    : SubmitTxWithNonce(tx, txHandlingOptions, senderGiven);

            return new ValueTask<(Hash256, AcceptTxResult?)>((tx.Hash!, result)); // The sealer calculates the hash
        }

        private AcceptTxResult SubmitTxWithNonce(Transaction tx, TxHandlingOptions txHandlingOptions, bool senderGiven)
        {
            using NonceLocker locker = _nonceManager.TxWithNonceReceived(tx.SenderAddress!, tx.Nonce);
            return SubmitTx(locker, tx, txHandlingOptions, senderGiven);
        }

        private AcceptTxResult SubmitTxWithManagedNonce(Transaction tx, TxHandlingOptions txHandlingOptions, bool senderGiven)
        {
            using NonceLocker locker = _nonceManager.ReserveNonce(tx.SenderAddress!, _txPool, out ulong reservedNonce);
            txHandlingOptions |= TxHandlingOptions.AllowReplacingSignature;
            tx.Nonce = reservedNonce;
            return SubmitTx(locker, tx, txHandlingOptions, senderGiven);
        }

        private AcceptTxResult SubmitTx(NonceLocker locker, Transaction tx, TxHandlingOptions txHandlingOptions, bool senderGiven)
        {
            AcceptTxResult result = SubmitTx(tx, txHandlingOptions, senderGiven);

            if (result == AcceptTxResult.Accepted)
            {
                locker.Accept(tx);
            }

            return result;
        }

        // Every node runs a transaction as the account its signature recovers, and the pool keeps a sender it is given,
        // so a given sender that is not that account is refused. A frame transaction has no r/s signature: its sender is
        // part of its payload, and r/s it carries are not used.
        private AcceptTxResult SubmitTx(Transaction tx, TxHandlingOptions txHandlingOptions, bool senderGiven) =>
            !_sealer.TrySeal(tx, txHandlingOptions) ? AcceptTxResult.SignFailed
            : senderGiven && tx.Signature is not null && !tx.SupportsFrames && !IsSignedBySender(tx) ? SenderIsNotSigner
            : _txPool.SubmitTx(tx, txHandlingOptions);

        private bool IsSignedBySender(Transaction tx) =>
            _ecdsa.TryRecoverAddress(tx, out Address? signer, UsesSignatureChainId()) && signer == tx.SenderAddress;

        // Where the spec does not validate chain ids, the pool accepts a legacy signature for another chain, and block
        // processing recovers it over the chain id it carries.
        private bool UsesSignatureChainId() => !_specProvider.GetCurrentHeadSpec().ValidateChainId;
    }
}

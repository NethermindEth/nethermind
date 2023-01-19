// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;

namespace Nethermind.TxPool
{
    // TODO: this should be nonce reserving tx sender, not sealer?
    public class NonceReservingTxSealer : TxSealer
    {
        private readonly ITxPool _txPool;
        private readonly IEthereumEcdsa _ecdsa;

        public NonceReservingTxSealer(ITxSigner txSigner,
            ITimestamper timestamper,
            ITxPool txPool,
            IEthereumEcdsa ecdsa)
            : base(txSigner, timestamper)
        {
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
        }

        public override ValueTask Seal(Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            bool manageNonce = (txHandlingOptions & TxHandlingOptions.ManagedNonce) == TxHandlingOptions.ManagedNonce;
            if (manageNonce)
            {
                tx.SenderAddress ??= _ecdsa.RecoverAddress(tx);
                if (tx.SenderAddress is null)
                    throw new ArgumentNullException(nameof(tx.SenderAddress));
                tx.Nonce = _txPool.ReserveOwnTransactionNonce(tx.SenderAddress);
                txHandlingOptions |= TxHandlingOptions.AllowReplacingSignature;
            }

            return base.Seal(tx, txHandlingOptions);
        }
    }
}

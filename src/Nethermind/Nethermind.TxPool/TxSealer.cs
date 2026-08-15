// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Crypto;

namespace Nethermind.TxPool
{
    public class TxSealer(ITxSigner txSigner, ITimestamper timestamper) : ITxSealer
    {
        private readonly ITxSigner _txSigner = txSigner ?? throw new ArgumentNullException(nameof(txSigner));
        private readonly ITimestamper _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));

        public virtual bool TrySeal(Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            bool allowChangeExistingSignature = (txHandlingOptions & TxHandlingOptions.AllowReplacingSignature) == TxHandlingOptions.AllowReplacingSignature;
            // Frame transactions (EIP-8141) carry authorization in their per-frame signatures list and
            // have no top-level ECDSA signature. Their Signature is always null by design, so the
            // sealer must not attempt to sign them (doing so returns SignFailed for a pre-signed
            // eth_sendRawTransaction submission).
            if (!tx.SupportsFrames && (tx.Signature is null || allowChangeExistingSignature))
            {
                if (!_txSigner.TrySign(tx)) return false;
            }

            tx.Hash = tx.CalculateHash();
            tx.Timestamp = _timestamper.UnixTime.Seconds;
            return true;
        }
    }
}

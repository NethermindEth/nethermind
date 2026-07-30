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
            if (tx.Signature is null || allowChangeExistingSignature)
            {
                if (!_txSigner.TrySign(tx)) return false;
            }

            tx.Hash = tx.CalculateHash();
            tx.Timestamp = _timestamper.UnixTime.Seconds;
            return true;
        }
    }
}

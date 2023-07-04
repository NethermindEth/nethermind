// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Crypto;

namespace Nethermind.TxPool
{
    public class TxSealer : ITxSealer
    {
        private readonly ITxSigner _txSigner;
        private readonly ITimestamper _timestamper;

        public TxSealer(ITxSigner txSigner, ITimestamper timestamper)
        {
            _txSigner = txSigner ?? throw new ArgumentNullException(nameof(txSigner));
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
        }

        public virtual ValueTask Seal(Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            bool allowChangeExistingSignature = (txHandlingOptions & TxHandlingOptions.AllowReplacingSignature) == TxHandlingOptions.AllowReplacingSignature;
            if (tx.Signature is null || allowChangeExistingSignature)
            {
                _txSigner.Sign(tx);
            }

            tx.Hash = tx.CalculateHash();
            tx.Timestamp = _timestamper.UnixTime.Seconds;

            return ValueTask.CompletedTask;
        }
    }
}

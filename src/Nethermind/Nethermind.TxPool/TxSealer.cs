//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
            if (tx.Signature == null || allowChangeExistingSignature)
            {
                _txSigner.Sign(tx);
            }

            tx.Hash = tx.CalculateHash();
            tx.Timestamp = _timestamper.UnixTime.Seconds;
            
            return ValueTask.CompletedTask;
        }
    }
}

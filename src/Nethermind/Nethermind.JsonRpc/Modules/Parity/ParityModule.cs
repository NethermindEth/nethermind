/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Linq;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Parity
{
    public class ParityModule : IParityModule
    {
        private readonly IEcdsa _ecdsa;
        private readonly ITxPool _txPool;

        public ParityModule(IEcdsa ecdsa, ITxPool txPool, ILogManager logManager)
        {
            _ecdsa = ecdsa;
            _txPool = txPool;
        }

        public ResultWrapper<ParityTransaction[]> parity_pendingTransactions()
            => ResultWrapper<ParityTransaction[]>.Success(_txPool.GetPendingTransactions().Where(pt => pt.SenderAddress != null)
                .Select(t => new ParityTransaction(t, Rlp.Encode(t).Bytes,
                    t.IsSigned ? _ecdsa.RecoverPublicKey(t.Signature, t.Hash) : null)).ToArray());
    }
}
//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Blockchain.Processing
{
    public class TxSignaturesRecoveryStep : IBlockDataRecoveryStep
    {
        private readonly IEthereumEcdsa _ecdsa;
        private readonly ITxPool _txPool;
        private readonly ILogger _logger;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="ecdsa">Needed to recover an address from a signature.</param>
        /// <param name="txPool">Finding transactions in mempool can speed up address recovery.</param>
        /// <param name="logManager">Logging</param>
        public TxSignaturesRecoveryStep(IEthereumEcdsa ecdsa, ITxPool txPool, ILogManager logManager)
        {
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(ecdsa));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        public void RecoverData(Block block)
        {
            if (block.Transactions.Length == 0 || block.Transactions[0].SenderAddress != null)
            {
                return;
            }
            
            for (int i = 0; i < block.Transactions.Length; i++)
            {
                _txPool.TryGetPendingTransaction(block.Transactions[i].Hash, out var transaction);
                Address sender = transaction?.SenderAddress;
                
                block.Transactions[i].SenderAddress = sender ?? _ecdsa.RecoverAddress(block.Transactions[i]);
                if(_logger.IsTrace) _logger.Trace($"Recovered {block.Transactions[i].SenderAddress} sender for {block.Transactions[i].Hash} (tx pool cached value: {sender})");
            }
        }
    }
}
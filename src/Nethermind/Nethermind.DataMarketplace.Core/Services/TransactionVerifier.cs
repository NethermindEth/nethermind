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

using System.Threading.Tasks;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Core.Services
{
    public class TransactionVerifier : ITransactionVerifier
    {
        private readonly INdmBlockchainBridge _blockchainBridge;
        private readonly uint _requiredBlockConfirmations;

        public TransactionVerifier(INdmBlockchainBridge blockchainBridge, uint requiredBlockConfirmations)
        {
            _blockchainBridge = blockchainBridge;
            _requiredBlockConfirmations = requiredBlockConfirmations;
        }
        
        public async Task<TransactionVerifierResult> VerifyAsync(NdmTransaction transaction)
        {
            var confirmations = 0;
            var latestBlock = await _blockchainBridge.GetLatestBlockAsync();
            if (latestBlock is null)
            {
                return new TransactionVerifierResult(false, 0, _requiredBlockConfirmations);
            }

            do
            {
                confirmations++;
                if (latestBlock.Hash == transaction.BlockHash)
                {
                    break;
                }

                latestBlock = await _blockchainBridge.FindBlockAsync(latestBlock.ParentHash);
                if (latestBlock is null)
                {
                    confirmations = 0;
                    break;
                }
                
                if (confirmations == _requiredBlockConfirmations)
                {
                    break;
                }
                
            } while (confirmations < _requiredBlockConfirmations);
            
            return new TransactionVerifierResult(true, confirmations, _requiredBlockConfirmations);
        }
    }
}
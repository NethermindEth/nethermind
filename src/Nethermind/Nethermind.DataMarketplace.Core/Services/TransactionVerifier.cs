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

using Nethermind.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Facade;

namespace Nethermind.DataMarketplace.Core.Services
{
    public class TransactionVerifier : ITransactionVerifier
    {
        private readonly IBlockchainBridge _blockchainBridge;
        private readonly uint _requiredBlockConfirmations;

        public TransactionVerifier(IBlockchainBridge blockchainBridge, uint requiredBlockConfirmations)
        {
            _blockchainBridge = blockchainBridge;
            _requiredBlockConfirmations = requiredBlockConfirmations;
        }
        
        public TransactionVerifierResult Verify(TxReceipt receipt)
        {
            var confirmations = 0;
            var block = _blockchainBridge.FindBlock(_blockchainBridge.Head.Hash);
            if (block is null)
            {
                return new TransactionVerifierResult(false, 0, _requiredBlockConfirmations);
            }

            while (block.Number >= receipt.BlockNumber)
            {
                confirmations++;
                if (block.Hash == receipt.BlockHash)
                {
                    break;
                }

                block = _blockchainBridge.FindBlock(block.ParentHash);
                if (block is null)
                {
                    break;
                }
            }
            
            return new TransactionVerifierResult(true, confirmations, _requiredBlockConfirmations);
        }
    }
}
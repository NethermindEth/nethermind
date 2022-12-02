// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
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
            int confirmations = 0;
            Block? latestBlock = await _blockchainBridge.GetLatestBlockAsync();
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

                if (latestBlock.ParentHash is null)
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

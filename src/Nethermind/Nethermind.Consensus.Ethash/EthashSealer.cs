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

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;

[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]

namespace Nethermind.Consensus.Ethash
{
    public class EthashSealer : ISealer
    {
        private readonly IEthash _ethash;
        private readonly ISigner _signer;
        private readonly ILogger _logger;

        public EthashSealer(IEthash ethash, ISigner signer, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));            
            _ethash = ethash ?? throw new ArgumentNullException(nameof(ethash));
            _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        }

        public async Task<Block> SealBlock(Block processed, CancellationToken cancellationToken)
        {
            Block? block = await MineAsync(cancellationToken, processed, null).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.Error($"{nameof(SealBlock)} failed", t.Exception);
                    return null;
                }

                return t.Result;
            }, cancellationToken);

            if (block is null)
            {
                throw new SealEngineException($"{nameof(SealBlock)} failed");
            }

            return block;
        }

        public bool CanSeal(long blockNumber, Keccak parentHash)
        {
            return true;
        }

        public Address Address => _signer.Address;

        internal async Task<Block> MineAsync(CancellationToken cancellationToken, Block processed, ulong? startNonce)
        {
            if (processed.Header.TxRoot == null ||
                processed.Header.StateRoot == null ||
                processed.Header.ReceiptsRoot == null ||
                processed.Header.OmmersHash == null ||
                processed.Header.Bloom == null ||
                processed.Header.ExtraData == null)
            {
                throw new InvalidOperationException($"Requested to mine an invalid block {processed.Header}");
            }

            Task<Block> miningTask = Task.Factory.StartNew(() => Mine(processed, startNonce), cancellationToken);
            await miningTask.ContinueWith(
                t =>
                {
                    if (t.IsCompleted)
                    {
                        t.Result.Header.Hash = t.Result.Header.CalculateHash();
                    }
                }, cancellationToken, TaskContinuationOptions.NotOnFaulted, TaskScheduler.Default);

            return await miningTask;
        }

        private Block Mine(Block block, ulong? startNonce)
        {
            (Keccak MixHash, ulong Nonce) result = _ethash.Mine(block.Header, startNonce);
            block.Header.Nonce = result.Nonce;
            block.Header.MixHash = result.MixHash;
            return block;
        }
    }
}

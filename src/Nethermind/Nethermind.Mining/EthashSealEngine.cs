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

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Mining.Difficulty;

[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]

namespace Nethermind.Mining
{
    public class EthashSealEngine : ISealEngine
    {
        private readonly IEthash _ethash;
        private readonly IDifficultyCalculator _difficultyCalculator;
        private readonly ILogger _logger;

        public EthashSealEngine(IEthash ethash, IDifficultyCalculator difficultyCalculator, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _difficultyCalculator = difficultyCalculator ?? throw new ArgumentNullException(nameof(difficultyCalculator));
            _ethash = ethash ?? throw new ArgumentNullException(nameof(ethash));
        }

        public BigInteger MinGasPrice { get; set; } = 0;

        public async Task<Block> MineAsync(Block processed, CancellationToken cancellationToken)
        {
            Block block = await MineAsync(cancellationToken, processed, null).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.Error($"{nameof(MineAsync)} failed", t.Exception);
                    return null;
                }

                return t.Result;
            }, cancellationToken);

            if (block == null)
            {
                throw new SealEngineException($"{nameof(MineAsync)} failed");
            }

            return block;
        }

        public bool ValidateSeal(BlockHeader header)
        {
            // TODO: all until we properly optimize ethash, still with sensible security measures (although there are many attack vectors for this particular node during sync)
            if (header.Number < 750000)
            {
                return true;
            }

            if (header.Number < 6500000 && header.Number % 30000 != 0) // TODO: this numbers are here to secure mainnet only (current block and epoch length) 
            {
                return true;
            }
            
            return _ethash.Validate(header);
        }
        
        public bool ValidateParams(Block parent, BlockHeader header)
        {   
            bool extraDataNotTooLong = header.ExtraData.Length <= 32;
            if (!extraDataNotTooLong)
            {
                _logger.Warn($"Invalid block header ({header.Hash}) - extra data too long {header.ExtraData.Length}");
                return false;
            }
            
            UInt256 difficulty = _difficultyCalculator.Calculate(parent.Header.Difficulty, parent.Header.Timestamp, header.Timestamp, header.Number, parent.Ommers.Length > 0);
            bool isDifficultyCorrect = difficulty == header.Difficulty;
            if (!isDifficultyCorrect)
            {
                _logger.Warn($"Invalid block header ({header.Hash}) - incorrect diffuclty {header.Difficulty} instead of {difficulty}");
                return false;
            }

            return true;
        }

        public bool IsMining { get; set; }

        internal async Task<Block> MineAsync(CancellationToken cancellationToken, Block processed, ulong? startNonce)
        {
            if (processed.Header.TransactionsRoot == null ||
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
                        t.Result.Header.Hash = BlockHeader.CalculateHash(t.Result.Header);
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
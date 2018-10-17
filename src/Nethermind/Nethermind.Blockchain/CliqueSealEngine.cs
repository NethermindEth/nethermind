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
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Mining;

[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]

namespace Nethermind.Blockchain
{
    public class CliqueSealEngine : ISealEngine
    {
        private readonly Clique _clique;
        private readonly ILogger _logger;

        public CliqueSealEngine(Clique clique, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _clique = clique;
        }

        public BigInteger MinGasPrice { get; set; } = 0;

        public async Task<Block> MineAsync(Block processed, CancellationToken cancellationToken)
        {
            _logger.Info("MineAsync");
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
                // TODO throw
                // throw new SealEngineException($"{nameof(MineAsync)} failed");
                return null;
            }

            return block;
        }

        public bool ValidateParams(Block parent, BlockHeader header)
        {
            return _clique.VerifyHeader(header);
        }

        public bool ValidateSeal(BlockHeader header)
        {
            return _clique.VerifySeal(header, null);
        }

        public bool IsMining { get; set; }

        private async Task<Block> MineAsync(CancellationToken cancellationToken, Block processed, ulong? startNonce)
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

            Task<Block> miningTask = Task.Factory.StartNew(() => Mine(processed), cancellationToken);
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

        private Block Mine(Block block)
        {
            return _clique.Mine(block);
        }
    }
}
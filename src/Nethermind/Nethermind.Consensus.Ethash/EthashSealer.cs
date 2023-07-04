// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;

namespace Nethermind.Consensus.Ethash
{
    internal class EthashSealer : ISealer
    {
        private readonly IEthash _ethash;
        private readonly ISigner _signer;
        private readonly ILogger _logger;

        internal EthashSealer(IEthash? ethash, ISigner? signer, ILogManager? logManager)
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
            if (processed.Header.TxRoot is null ||
                processed.Header.StateRoot is null ||
                processed.Header.ReceiptsRoot is null ||
                processed.Header.UnclesHash is null ||
                processed.Header.Bloom is null ||
                processed.Header.ExtraData is null)
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

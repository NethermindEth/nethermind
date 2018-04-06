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

using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Mining;

namespace Nethermind.Blockchain
{
    public class EthashSealEngine : ISealEngine
    {
        private readonly IEthash _ethash;

        public EthashSealEngine(IEthash ethash)
        {
            _ethash = ethash;
        }

        public BigInteger MinGasPrice { get; set; } = 0;

        public async Task<Block> MineAsync(Block processed)
        {
            return await MineAsync(processed, null);
        }

        public bool Validate(BlockHeader header)
        {
            return _ethash.Validate(header);
        }

        public bool IsOn { get; set; }

        public async Task<Block> MineAsync(Block processed, ulong? startNonce)
        {
            Debug.Assert(processed.Header.TransactionsRoot != null, "transactions root");
            Debug.Assert(processed.Header.StateRoot != null, "state root");
            Debug.Assert(processed.Header.ReceiptsRoot != null, "receipts root");
            Debug.Assert(processed.Header.OmmersHash != null, "ommers hash");
            Debug.Assert(processed.Header.Bloom != null, "bloom");
            Debug.Assert(processed.Header.ExtraData != null, "extra data");

            Block minedBlock = await Task.Factory.StartNew(() => Mine(processed, startNonce));
            minedBlock.Header.RecomputeHash();
            return minedBlock;
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
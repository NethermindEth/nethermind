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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;

namespace Nethermind.Blockchain
{
    public interface IBlockchainProcessor
    {
        void Start();
        Task StopAsync(bool processRemainingBlocks = false);
        void Process(Block block); // TODO: tks: should be queued only
        
        /// <summary>
        /// Executes a block from the past, stores receipts and tx hash -> block number mapping.
        /// </summary>
        /// <param name="block"></param>
        void AddTxData(Block block); // TODO: tks: should be queued
        TransactionTrace Trace(Keccak txHash); // TODO: tks: should be queued
        TransactionTrace Trace(UInt256 blockNumber, int txIndex); // TODO: tks: should be queued
        BlockTrace TraceBlock(Keccak blokHash); // TODO: tks: should be queued
        BlockTrace TraceBlock(UInt256 blokNumber); // TODO: tks: should be queued
    }
}
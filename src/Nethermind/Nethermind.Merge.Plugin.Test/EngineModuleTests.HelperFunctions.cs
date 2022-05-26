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
// 

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Merge.Plugin.Data;
using NUnit.Framework;
using Nethermind.Int256;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data.V1;
using Nethermind.State;

namespace Nethermind.Merge.Plugin.Test
{
    [Parallelizable(ParallelScope.All)]
    public partial class EngineModuleTests
    {
        private static readonly DateTime Timestamp = DateTimeOffset.FromUnixTimeSeconds(1000).UtcDateTime;
        private ITimestamper Timestamper { get; } = new ManualTimestamper(Timestamp);
        
        private void AssertExecutionStatusChanged(IEngineRpcModule rpc, Keccak headBlockHash, Keccak finalizedBlockHash,
             Keccak safeBlockHash)
        {
            ExecutionStatusResult? result = rpc.engine_executionStatus().Data;
            Assert.AreEqual(headBlockHash, result.HeadBlockHash);
            Assert.AreEqual(finalizedBlockHash, result.FinalizedBlockHash);
             Assert.AreEqual(safeBlockHash, result.SafeBlockHash);
        }
        
        private (UInt256, UInt256) AddTransactions(MergeTestBlockchain chain, BlockRequestResult executePayloadRequest,
            PrivateKey from, Address to, uint count, int value, out BlockHeader parentHeader)
        {
            Transaction[] transactions = BuildTransactions(chain, executePayloadRequest.ParentHash, from, to, count, value,
                out Account accountFrom, out parentHeader);
            executePayloadRequest.SetTransactions(transactions);
            UInt256 totalValue = ((int)(count * value)).GWei();
            return (accountFrom.Balance - totalValue,
                chain.StateReader.GetBalance(parentHeader.StateRoot!, to) + totalValue);
        }

        private Transaction[] BuildTransactions(MergeTestBlockchain chain, Keccak parentHash, PrivateKey from,
            Address to, uint count, int value, out Account accountFrom, out BlockHeader parentHeader)
        {
            Transaction BuildTransaction(uint index, Account senderAccount) =>
                Build.A.Transaction.WithNonce(senderAccount.Nonce + index)
                    .WithTimestamp(Timestamper.UnixTime.Seconds)
                    .WithTo(to)
                    .WithValue(value.GWei())
                    .WithGasPrice(1.GWei())
                    .WithChainId(chain.SpecProvider.ChainId)
                    .WithSenderAddress(from.Address)
                    .SignedAndResolved(from)
                    .TestObject;

            parentHeader = chain.BlockTree.FindHeader(parentHash, BlockTreeLookupOptions.None)!;
            Account account = chain.StateReader.GetAccount(parentHeader.StateRoot!, @from.Address)!;
            accountFrom = account;

            return Enumerable.Range(0, (int)count)
                .Select(i => BuildTransaction((uint)i, account)).ToArray();
        }

        private BlockRequestResult CreateParentBlockRequestOnHead(IBlockTree blockTree)
        {
            Block? head = blockTree.Head;
            if (head == null) throw new NotSupportedException();
            return new BlockRequestResult()
            {
                BlockNumber = head.Number, BlockHash = head.Hash!, StateRoot = head.StateRoot!, ReceiptsRoot = head.ReceiptsRoot!, GasLimit = head.GasLimit, Timestamp = (ulong)head.Timestamp
            };
        }

        private static BlockRequestResult CreateBlockRequest(BlockRequestResult parent, Address miner)
        {
            BlockRequestResult blockRequest = new()
            {
                ParentHash = parent.BlockHash,
                FeeRecipient = miner,
                StateRoot = parent.StateRoot,
                BlockNumber = parent.BlockNumber + 1,
                GasLimit = parent.GasLimit,
                GasUsed = 0,
                ReceiptsRoot = Keccak.EmptyTreeHash,
                LogsBloom = Bloom.Empty,
                Timestamp = parent.Timestamp + 1,
            };

            blockRequest.SetTransactions(Array.Empty<Transaction>());
            TryCalculateHash(blockRequest, out Keccak? hash);
            blockRequest.BlockHash = hash;
            return blockRequest;
        }

        private static BlockRequestResult[] CreateBlockRequestBranch(BlockRequestResult parent, Address miner, int count)
        {
            BlockRequestResult currentBlock = parent;
            BlockRequestResult[] blockRequests = new BlockRequestResult[count];
            for (int i = 0; i < count; i++)
            {
                currentBlock = CreateBlockRequest(currentBlock, miner);
                blockRequests[i] = currentBlock;
            }

            return blockRequests;
        }

        private Block? RunForAllBlocksInBranch(IBlockTree blockTree, Keccak blockHash, Func<Block, bool> shouldStop,
            bool requireCanonical)
        {
            BlockTreeLookupOptions options =
                requireCanonical ? BlockTreeLookupOptions.RequireCanonical : BlockTreeLookupOptions.None;
            Block? current = blockTree.FindBlock(blockHash, options);
            while (current is not null && !shouldStop(current))
            {
                current = blockTree.FindParent(current, options);
            }

            return current;
        }

        private static TestCaseData GetNewBlockRequestBadDataTestCase<T>(
            Expression<Func<BlockRequestResult, T>> propertyAccess, T wrongValue)
        {
            Action<BlockRequestResult, T> setter = propertyAccess.GetSetter();
            // ReSharper disable once ConvertToLocalFunction
            Action<BlockRequestResult> wrongValueSetter = r => setter(r, wrongValue);
            return new TestCaseData(wrongValueSetter)
            {
                TestName =
                    $"executePayload_rejects_incorrect_{propertyAccess.GetName().ToLower()}({wrongValue?.ToString()})"
            };
        }

        private static bool TryCalculateHash(BlockRequestResult request, out Keccak hash)
        {
            if (request.TryGetBlock(out Block? block) && block != null)
            {
                hash = block.CalculateHash();
                return true;
            }
            else
            {
                hash = Keccak.Zero;
                return false;
            }
        }
        
        private async Task<TestRpcBlockchain> CreateTestRpc(MergeTestBlockchain chain)
        {
            SingleReleaseSpecProvider spec = new(London.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
                .WithBlockFinder(chain.BlockFinder)
                .Build(spec);
            return testRpc;
        }
    }
}      

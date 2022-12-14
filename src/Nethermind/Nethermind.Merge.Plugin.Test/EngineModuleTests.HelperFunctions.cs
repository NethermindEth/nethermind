// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
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
using Nethermind.State;

namespace Nethermind.Merge.Plugin.Test
{
    [Parallelizable(ParallelScope.All)]
    public partial class EngineModuleTests
    {
        private static readonly DateTime Timestamp = DateTimeOffset.FromUnixTimeSeconds(1000).UtcDateTime;
        private ITimestamper Timestamper { get; } = new ManualTimestamper(Timestamp);

        private void AssertExecutionStatusChanged(IBlockFinder blockFinder, Keccak headBlockHash, Keccak finalizedBlockHash,
             Keccak safeBlockHash)
        {
            Assert.AreEqual(headBlockHash, blockFinder.HeadHash);
            Assert.AreEqual(finalizedBlockHash, blockFinder.FinalizedHash);
            Assert.AreEqual(safeBlockHash, blockFinder.SafeHash);
        }

        private (UInt256, UInt256) AddTransactions(MergeTestBlockchain chain, ExecutionPayload executePayloadRequest,
            PrivateKey from, Address to, uint count, int value, out BlockHeader parentHeader)
        {
            Transaction[] transactions = BuildTransactions(chain, executePayloadRequest.ParentHash, from, to, count, value, out Account accountFrom, out parentHeader);
            executePayloadRequest.SetTransactions(transactions);
            UInt256 totalValue = ((int)(count * value)).GWei();
            return (accountFrom.Balance - totalValue, chain.StateReader.GetBalance(parentHeader.StateRoot!, to) + totalValue);
        }

        private Transaction[] BuildTransactions(MergeTestBlockchain chain, Keccak parentHash, PrivateKey from,
            Address to, uint count, int value, out Account accountFrom, out BlockHeader parentHeader, int blobCountPerTx = 0)
        {
            Transaction BuildTransaction(uint index, Account senderAccount) =>
                Build.A.Transaction.WithNonce(senderAccount.Nonce + index)
                    .WithTimestamp(Timestamper.UnixTime.Seconds)
                    .WithTo(to)
                    .WithValue(value.GWei())
                    .WithGasPrice(1.GWei())
                    .WithMaxFeePerGas(1.GWei())
                    .WithChainId(chain.SpecProvider.ChainId)
                    .WithSenderAddress(from.Address)
                    .WithShardBlobTxFields(blobCountPerTx)
                    .SignedAndResolved(from)
                    .TestObject;

            parentHeader = chain.BlockTree.FindHeader(parentHash, BlockTreeLookupOptions.None)!;
            Account account = chain.StateReader.GetAccount(parentHeader.StateRoot!, from.Address)!;
            accountFrom = account;

            return Enumerable.Range(0, (int)count).Select(i => BuildTransaction((uint)i, account)).ToArray();
        }

        private ExecutionPayload CreateParentBlockRequestOnHead(IBlockTree blockTree)
        {
            Block? head = blockTree.Head;
            if (head is null) throw new NotSupportedException();
            return new ExecutionPayload()
            {
                BlockNumber = head.Number,
                BlockHash = head.Hash!,
                StateRoot = head.StateRoot!,
                ReceiptsRoot = head.ReceiptsRoot!,
                GasLimit = head.GasLimit,
                Timestamp = head.Timestamp,
                BaseFeePerGas = head.BaseFeePerGas,
            };
        }

        private static ExecutionPayload CreateBlockRequest(ExecutionPayload parent, Address miner, IList<Withdrawal>? withdrawals = null, UInt256? excessDataGas = null)
        {
            ExecutionPayload blockRequest = new()
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
                Withdrawals = withdrawals,
                ExcessDataGas = excessDataGas,
                BaseFeePerGas = 1,
            };

            blockRequest.SetTransactions(Array.Empty<Transaction>());
            TryCalculateHash(blockRequest, out Keccak? hash);
            blockRequest.BlockHash = hash;
            return blockRequest;
        }

        private static ExecutionPayload[] CreateBlockRequestBranch(ExecutionPayload parent, Address miner, int count)
        {
            ExecutionPayload currentBlock = parent;
            ExecutionPayload[] blockRequests = new ExecutionPayload[count];
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
            BlockTreeLookupOptions options = requireCanonical ? BlockTreeLookupOptions.RequireCanonical : BlockTreeLookupOptions.None;
            Block? current = blockTree.FindBlock(blockHash, options);
            while (current is not null && !shouldStop(current))
            {
                current = blockTree.FindParent(current, options);
            }

            return current;
        }

        private static TestCaseData GetNewBlockRequestBadDataTestCase<T>(
            Expression<Func<ExecutionPayload, T>> propertyAccess, T wrongValue)
        {
            Action<ExecutionPayload, T> setter = propertyAccess.GetSetter();
            // ReSharper disable once ConvertToLocalFunction
            Action<ExecutionPayload> wrongValueSetter = r => setter(r, wrongValue);
            return new TestCaseData(wrongValueSetter)
            {
                TestName = $"executePayload_rejects_incorrect_{propertyAccess.GetName().ToLower()}({wrongValue?.ToString()})"
            };
        }

        private static bool TryCalculateHash(ExecutionPayload request, out Keccak hash)
        {
            if (request.TryGetBlock(out Block? block) && block is not null)
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
            TestSingleReleaseSpecProvider spec = new(London.Instance);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
                .WithBlockFinder(chain.BlockFinder)
                .Build(spec);
            return testRpc;
        }
    }
}

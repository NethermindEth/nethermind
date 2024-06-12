// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
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
using Nethermind.Consensus.BeaconBlockRoot;

namespace Nethermind.Merge.Plugin.Test
{
    [Parallelizable(ParallelScope.All)]
    public partial class EngineModuleTests
    {
        private static readonly DateTime Timestamp = DateTimeOffset.FromUnixTimeSeconds(1000).UtcDateTime;
        private static readonly IBeaconBlockRootHandler _beaconBlockRootHandler = new BeaconBlockRootHandler();
        private ITimestamper Timestamper { get; } = new ManualTimestamper(Timestamp);
        private void AssertExecutionStatusChanged(IBlockFinder blockFinder, Hash256 headBlockHash, Hash256 finalizedBlockHash,
             Hash256 safeBlockHash)
        {
            Assert.That(blockFinder.HeadHash, Is.EqualTo(headBlockHash));
            Assert.That(blockFinder.FinalizedHash, Is.EqualTo(finalizedBlockHash));
            Assert.That(blockFinder.SafeHash, Is.EqualTo(safeBlockHash));
        }

        private (UInt256, UInt256) AddTransactions(MergeTestBlockchain chain, ExecutionPayload executePayloadRequest,
            PrivateKey from, Address to, uint count, int value, out BlockHeader parentHeader)
        {
            Transaction[] transactions = BuildTransactions(chain, executePayloadRequest.ParentHash, from, to, count, value, out AccountStruct accountFrom, out parentHeader);
            executePayloadRequest.SetTransactions(transactions);
            UInt256 totalValue = ((int)(count * value)).GWei();
            return (accountFrom.Balance - totalValue, chain.StateReader.GetBalance(parentHeader.StateRoot!, to) + totalValue);
        }

        private Transaction[] BuildTransactions(MergeTestBlockchain chain, Hash256 parentHash, PrivateKey from,
            Address to, uint count, int value, out AccountStruct accountFrom, out BlockHeader parentHeader, int blobCountPerTx = 0)
        {
            Transaction BuildTransaction(uint index, AccountStruct senderAccount)
            {
                TransactionBuilder<Transaction> builder = Build.A.Transaction
                    .WithNonce(senderAccount.Nonce + index)
                    .WithTimestamp(Timestamper.UnixTime.Seconds)
                    .WithTo(to)
                    .WithValue(value.GWei())
                    .WithGasPrice(1.GWei())
                    .WithChainId(chain.SpecProvider.ChainId)
                    .WithSenderAddress(from.Address);

                if (blobCountPerTx != 0)
                {
                    builder = builder.WithShardBlobTxTypeAndFields(blobCountPerTx);
                }
                else
                {
                    builder = builder.WithType(TxType.EIP1559);
                }

                return builder
                    .WithMaxFeePerGasIfSupports1559(1.GWei())
                    .SignedAndResolved(from).TestObject;
            }

            parentHeader = chain.BlockTree.FindHeader(parentHash, BlockTreeLookupOptions.None)!;
            chain.StateReader.TryGetAccount(parentHeader.StateRoot!, from.Address, out AccountStruct account);
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

        private static ExecutionPayload CreateBlockRequest(MergeTestBlockchain chain, ExecutionPayload parent, Address miner, Withdrawal[]? withdrawals = null,
               ulong? blobGasUsed = null, ulong? excessBlobGas = null, Transaction[]? transactions = null, Hash256? parentBeaconBlockRoot = null)
        {
            ExecutionPayload blockRequest = CreateBlockRequestInternal<ExecutionPayload>(parent, miner, withdrawals, blobGasUsed, excessBlobGas, transactions: transactions, parentBeaconBlockRoot: parentBeaconBlockRoot);
            blockRequest.TryGetBlock(out Block? block);

            Snapshot before = chain.State.TakeSnapshot();
            chain.WithdrawalProcessor?.ProcessWithdrawals(block!, chain.SpecProvider.GenesisSpec);

            chain.State.Commit(chain.SpecProvider.GenesisSpec);
            chain.State.RecalculateStateRoot();
            blockRequest.StateRoot = chain.State.StateRoot;
            chain.State.Restore(before);

            TryCalculateHash(blockRequest, out Hash256? hash);
            blockRequest.BlockHash = hash;
            return blockRequest;
        }

        private static ExecutionPayloadV3 CreateBlockRequestV3(MergeTestBlockchain chain, ExecutionPayload parent, Address miner, Withdrawal[]? withdrawals = null,
                ulong? blobGasUsed = null, ulong? excessBlobGas = null, Transaction[]? transactions = null, Hash256? parentBeaconBlockRoot = null)
        {
            ExecutionPayloadV3 blockRequestV3 = CreateBlockRequestInternal<ExecutionPayloadV3>(parent, miner, withdrawals, blobGasUsed, excessBlobGas, transactions: transactions, parentBeaconBlockRoot: parentBeaconBlockRoot);
            blockRequestV3.TryGetBlock(out Block? block);

            Snapshot before = chain.State.TakeSnapshot();
            _beaconBlockRootHandler.ApplyContractStateChanges(block!, chain.SpecProvider.GenesisSpec, chain.State);
            var blockHashStore = new BlockhashStore(chain.BlockTree, chain.SpecProvider, chain.State);
            blockHashStore.ApplyHistoryBlockHashes(block!.Header);
            chain.WithdrawalProcessor?.ProcessWithdrawals(block!, chain.SpecProvider.GenesisSpec);

            chain.State.Commit(chain.SpecProvider.GenesisSpec);
            chain.State.RecalculateStateRoot();
            blockRequestV3.StateRoot = chain.State.StateRoot;
            chain.State.Restore(before);

            TryCalculateHash(blockRequestV3, out Hash256? hash);
            blockRequestV3.BlockHash = hash;
            return blockRequestV3;
        }

        private static T CreateBlockRequestInternal<T>(ExecutionPayload parent, Address miner, Withdrawal[]? withdrawals = null,
                ulong? blobGasUsed = null, ulong? excessBlobGas = null, Transaction[]? transactions = null, Hash256? parentBeaconBlockRoot = null) where T : ExecutionPayload, new()
        {
            T blockRequest = new()
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
                BlobGasUsed = blobGasUsed,
                ExcessBlobGas = excessBlobGas,
                ParentBeaconBlockRoot = parentBeaconBlockRoot,
            };

            blockRequest.SetTransactions(transactions ?? Array.Empty<Transaction>());
            TryCalculateHash(blockRequest, out Hash256? hash);
            blockRequest.BlockHash = hash;
            return blockRequest;
        }

        private static ExecutionPayload[] CreateBlockRequestBranch(MergeTestBlockchain chain, ExecutionPayload parent, Address miner, int count)
        {
            ExecutionPayload currentBlock = parent;
            ExecutionPayload[] blockRequests = new ExecutionPayload[count];
            for (int i = 0; i < count; i++)
            {
                currentBlock = CreateBlockRequest(chain, currentBlock, miner);
                blockRequests[i] = currentBlock;
            }

            return blockRequests;
        }

        private Block? RunForAllBlocksInBranch(IBlockTree blockTree, Hash256 blockHash, Func<Block, bool> shouldStop,
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

        private static bool TryCalculateHash(ExecutionPayload request, out Hash256 hash)
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

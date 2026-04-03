// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
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
using Nethermind.Evm.State;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Specs;
using Nethermind.State;

namespace Nethermind.Merge.Plugin.Test
{
    [Parallelizable(ParallelScope.All)]
    public partial class EngineModuleTests : BaseEngineModuleTests
    {
        private static readonly DateTime Timestamp = DateTimeOffset.FromUnixTimeSeconds(1000).UtcDateTime;
        private ITimestamper Timestamper { get; } = new ManualTimestamper(Timestamp);
        private void AssertExecutionStatusChanged(IBlockFinder blockFinder, Hash256 headBlockHash, Hash256 finalizedBlockHash,
            Hash256 safeBlockHash)
        {
            Assert.Multiple(() =>
            {
                Assert.That(blockFinder.HeadHash, Is.EqualTo(headBlockHash));
                Assert.That(blockFinder.FinalizedHash, Is.EqualTo(finalizedBlockHash));
                Assert.That(blockFinder.SafeHash, Is.EqualTo(safeBlockHash));
            });
        }

        private void AssertExecutionStatusNotChanged(IBlockFinder blockFinder, Hash256 headBlockHash,
            Hash256 finalizedBlockHash, Hash256 safeBlockHash)
        {
            Assert.Multiple(() =>
            {
                Assert.That(blockFinder.HeadHash, Is.Not.EqualTo(headBlockHash));
                Assert.That(blockFinder.FinalizedHash, Is.Not.EqualTo(finalizedBlockHash));
                Assert.That(blockFinder.SafeHash, Is.Not.EqualTo(safeBlockHash));
            });
        }

        private async Task GetPayload_should_fail_on_unknown_payload(int version)
        {
            using MergeTestBlockchain chain = await CreateBlockchain();
            IEngineRpcModule rpc = chain.EngineRpcModule;

            byte[] payloadId = Bytes.FromHexString("0x0");
            int errorCode = version switch
            {
                2 => (await rpc.engine_getPayloadV2(payloadId)).ErrorCode,
                3 => (await rpc.engine_getPayloadV3(payloadId)).ErrorCode,
                _ => throw new ArgumentOutOfRangeException(nameof(version))
            };
            errorCode.Should().Be(MergeErrorCodes.UnknownPayload);
        }

        private (UInt256, UInt256) AddTransactions(MergeTestBlockchain chain, ExecutionPayload executePayloadRequest,
            PrivateKey from, Address to, uint count, int value, out BlockHeader parentHeader)
        {
            Transaction[] transactions = BuildTransactions(chain, executePayloadRequest.ParentHash, from, to, count, value, out AccountStruct accountFrom, out parentHeader);
            executePayloadRequest.SetTransactions(transactions);
            UInt256 totalValue = ((int)(count * value)).GWei;
            return (accountFrom.Balance - totalValue, chain.StateReader.GetBalance(parentHeader, to) + totalValue);
        }

        private Transaction[] BuildTransactions(MergeTestBlockchain chain, Hash256 parentHash, PrivateKey from,
            Address to, uint count, int value, out AccountStruct accountFrom, out BlockHeader parentHeader,
            int blobCountPerTx = 0, IReleaseSpec? spec = null)
        {
            return BuildTransactions(chain.BlockTree, chain.SpecProvider, chain.StateReader, Timestamper, parentHash, from, to,
                count, value, out accountFrom, out parentHeader, blobCountPerTx, spec);
        }

        private static Transaction[] BuildTransactions(
            IBlockTree blockTree,
            ISpecProvider specProvider,
            IStateReader stateReader,
            ITimestamper timestamper,
            Hash256 parentHash, PrivateKey from, Address to, uint count, int value, out AccountStruct accountFrom, out BlockHeader parentHeader, int blobCountPerTx = 0, IReleaseSpec? spec = null)
        {
            Transaction BuildTransaction(uint index, AccountStruct senderAccount)
            {
                TransactionBuilder<Transaction> builder = Build.A.Transaction
                    .WithNonce(senderAccount.Nonce + index)
                    .WithTimestamp(timestamper.UnixTime.Seconds)
                    .WithTo(to)
                    .WithValue(value.GWei)
                    .WithGasPrice(1.GWei)
                    .WithChainId(specProvider.ChainId)
                    .WithSenderAddress(from.Address);

                if (blobCountPerTx != 0)
                {
                    builder = builder.WithShardBlobTxTypeAndFields(blobCountPerTx, spec: spec);
                }
                else
                {
                    builder = builder.WithType(TxType.EIP1559);
                }

                return builder
                    .WithMaxFeePerGasIfSupports1559(1.GWei)
                    .SignedAndResolved(from).TestObject;
            }

            parentHeader = blockTree.FindHeader(parentHash, BlockTreeLookupOptions.None)!;
            stateReader.TryGetAccount(parentHeader, from.Address, out AccountStruct account);
            accountFrom = account;

            return Enumerable.Range(0, (int)count).Select(i => BuildTransaction((uint)i, account)).ToArray();
        }

        private static ExecutionPayload CreateBlockRequest(MergeTestBlockchain chain, ExecutionPayload parent, Address miner, Withdrawal[]? withdrawals = null,
            ulong? blobGasUsed = null, ulong? excessBlobGas = null, Transaction[]? transactions = null, Hash256? parentBeaconBlockRoot = null)
        {
            using var overridableEnv = chain.WorldStateManager.CreateOverridableWorldScope();
            using var childContainer = chain.Container.BeginLifetimeScope(builder => builder.AddSingleton(overridableEnv.WorldState));

            return CreateBlockRequestInScope(childContainer, parent, miner, withdrawals, blobGasUsed, excessBlobGas, transactions, parentBeaconBlockRoot);
        }

        private static ExecutionPayload CreateBlockRequestInScope(IComponentContext childContainer, ExecutionPayload parent, Address miner, Withdrawal[]? withdrawals = null,
            ulong? blobGasUsed = null, ulong? excessBlobGas = null, Transaction[]? transactions = null, Hash256? parentBeaconBlockRoot = null)
        {
            IWorldState worldState = childContainer.Resolve<IWorldState>();
            ISpecProvider specProvider = childContainer.Resolve<ISpecProvider>();

            ExecutionPayload blockRequest = CreateBlockRequestInternal<ExecutionPayload>(parent, miner, withdrawals, blobGasUsed, excessBlobGas, transactions: transactions, parentBeaconBlockRoot: parentBeaconBlockRoot);
            Block? block = blockRequest.TryGetBlock().Block;

            using (var _ = worldState.BeginScope(parent.TryGetBlock().Block?.Header))
            {
                IWithdrawalProcessor withdrawalProcessor = childContainer.Resolve<IWithdrawalProcessor>();

                withdrawalProcessor?.ProcessWithdrawals(block!, specProvider.GenesisSpec);

                worldState.Commit(specProvider.GenesisSpec);
                worldState.RecalculateStateRoot();
                worldState.CommitTree(blockRequest.BlockNumber);
                blockRequest.StateRoot = worldState.StateRoot;
            }

            TryCalculateHash(blockRequest, out Hash256? hash);
            blockRequest.BlockHash = hash;
            return blockRequest;
        }

        private static ExecutionPayloadV3 CreateBlockRequestV3(
            MergeTestBlockchain chain,
            ExecutionPayload parent,
            Address miner,
            Withdrawal[]? withdrawals = null,
            ulong? blobGasUsed = null,
            ulong? excessBlobGas = null,
            Transaction[]? transactions = null,
            Hash256? parentBeaconBlockRoot = null)
        {
            ExecutionPayloadV3 blockRequestV3 = CreateBlockRequestInternal<ExecutionPayloadV3>(parent, miner, withdrawals, blobGasUsed, excessBlobGas, transactions: transactions, parentBeaconBlockRoot: parentBeaconBlockRoot);
            Block? block = blockRequestV3.TryGetBlock().Block;

            IWorldState globalWorldState = chain.MainWorldState;
            using (globalWorldState.BeginScope(parent.TryGetBlock().Block!.Header))
            {
                var blockHashStore = new BlockhashStore(globalWorldState);
                blockHashStore.ApplyBlockhashStateChanges(block!.Header, chain.SpecProvider.GetSpec(block.Header));
                chain.WithdrawalProcessor?.ProcessWithdrawals(block!, chain.SpecProvider.GenesisSpec);

                globalWorldState.Commit(chain.SpecProvider.GenesisSpec);
                globalWorldState.RecalculateStateRoot();
                blockRequestV3.StateRoot = globalWorldState.StateRoot;
            }

            TryCalculateHash(blockRequestV3, out Hash256? hash);
            blockRequestV3.BlockHash = hash;
            return blockRequestV3;
        }

        private static T CreateBlockRequestInternal<T>(ExecutionPayload parent, Address miner, Withdrawal[]? withdrawals = null,
            ulong? blobGasUsed = null, ulong? excessBlobGas = null, Transaction[]? transactions = null, Hash256? parentBeaconBlockRoot = null
        ) where T : ExecutionPayload, new()
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
                ParentBeaconBlockRoot = parentBeaconBlockRoot
            };

            blockRequest.SetTransactions(transactions ?? []);
            TryCalculateHash(blockRequest, out Hash256? hash);
            blockRequest.BlockHash = hash;
            return blockRequest;
        }

        private static ExecutionPayload[] CreateBlockRequestBranch(MergeTestBlockchain chain, ExecutionPayload parent, Address miner, int count)
        {
            ExecutionPayload currentBlock = parent;
            ExecutionPayload[] blockRequests = new ExecutionPayload[count];

            using var overridableEnv = chain.WorldStateManager.CreateOverridableWorldScope();
            using var childContainer = chain.Container.BeginLifetimeScope(builder => builder.AddSingleton(overridableEnv.WorldState));

            for (int i = 0; i < count; i++)
            {
                currentBlock = CreateBlockRequestInScope(childContainer, currentBlock, miner);
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
            Block? block = request.TryGetBlock().Block;
            if (block is not null)
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

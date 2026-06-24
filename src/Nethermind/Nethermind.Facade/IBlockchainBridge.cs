// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Facade.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Facade.Find;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.Int256;
using Nethermind.Trie;
using Block = Nethermind.Core.Block;

namespace Nethermind.Facade
{
    public interface IBlockchainBridge : ILogFinder
    {
        Block HeadBlock { get; }
        bool IsMining { get; }
        void RecoverTxSenders(Block block);
        Address? RecoverTxSender(Transaction tx);
        TxReceipt GetReceipt(Hash256 txHash);
        (TxReceipt? Receipt, ulong BlockTimestamp, TxGasInfo? GasInfo, int LogIndexStart) GetTxReceiptInfo(Hash256 txHash);
        bool TryGetTransaction(Hash256 txHash, [NotNullWhen(true)] out TransactionLookupResult? result, bool checkTxnPool = true);
        CallOutput Call(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride>? stateOverride = null, UInt256? blobBaseFeeOverride = null, BlockOverride? blockOverride = null, CancellationToken cancellationToken = default);
        SimulateOutput<TTrace> Simulate<TTrace>(BlockHeader header, SimulatePayload<TransactionWithSourceDetails> payload, ISimulateBlockTracerFactory<TTrace> simulateBlockTracerFactory, long gasCapLimit, CancellationToken cancellationToken);
        CallOutput EstimateGas(BlockHeader header, Transaction tx, int errorMarginBasisPoints, Dictionary<Address, AccountOverride>? stateOverride = null, UInt256? blobBaseFeeOverride = null, BlockOverride? blockOverride = null, CancellationToken cancellationToken = default);

        CallOutput CreateAccessList(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride>? stateOverride, bool optimize, UInt256? blobBaseFeeOverride = null, CancellationToken cancellationToken = default);
        ulong GetChainId();

        int NewBlockFilter();
        int NewPendingTransactionFilter();
        int NewFilter(BlockParameter fromBlock, BlockParameter toBlock, HashSet<AddressAsKey>? address = null, IEnumerable<Hash256[]?>? topics = null);
        void UninstallFilter(int filterId);
        bool FilterExists(int filterId);
        Hash256[] GetBlockFilterChanges(int filterId);
        Hash256[] GetPendingTransactionFilterChanges(int filterId);
        FilterLog[] GetLogFilterChanges(int filterId);
        FilterType GetFilterType(int filterId);
        LogFilter GetFilter(BlockParameter fromBlock, BlockParameter toBlock, HashSet<AddressAsKey>? addresses = null, IEnumerable<Hash256[]?>? topics = null);
        IEnumerable<FilterLog> GetLogs(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock, CancellationToken cancellationToken = default);
        IEnumerable<FilterLog> GetLogs(BlockParameter fromBlock, BlockParameter toBlock, HashSet<AddressAsKey>? addresses = null, IEnumerable<Hash256[]?>? topics = null, CancellationToken cancellationToken = default);

        bool TryGetLogs(int filterId, out IEnumerable<FilterLog> filterLogs, CancellationToken cancellationToken = default);
        /// <inheritdoc cref="Nethermind.State.IStateReader.RunTreeVisitor{TCtx}"/>
        void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock, VisitingStats? diagnostics = null) where TCtx : struct, INodeContext<TCtx>;

        bool HasStateForBlock(BlockHeader? baseBlock);

        Witness GenerateExecutionWitness(BlockHeader parent, Block block);
        SingleCallWitnessResult GenerateExecutionWitness(BlockHeader header, Transaction tx, CancellationToken cancellationToken = default);

        ReadOnlyBlockAccessList? GetBlockAccessList(long blockNumber, Hash256 blockHash);
        MemoryManager<byte>? GetBlockAccessListRlp(long blockNumber, Hash256 blockHash);
        void DeleteBlockAccessList(long blockNumber, Hash256 blockHash);
    }
}

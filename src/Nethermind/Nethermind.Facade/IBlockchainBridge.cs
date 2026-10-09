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
using Nethermind.Core.Collections;
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
        SimulateOutput<TTrace> Simulate<TTrace>(BlockHeader header, SimulatePayload<TransactionWithSourceDetails> payload, ISimulateBlockTracerFactory<TTrace> simulateBlockTracerFactory, ulong gasCapLimit, CancellationToken cancellationToken);
        CallOutput EstimateGas(BlockHeader header, Transaction tx, int errorMarginBasisPoints, Dictionary<Address, AccountOverride>? stateOverride = null, UInt256? blobBaseFeeOverride = null, BlockOverride? blockOverride = null, ulong gasCap = 0, CancellationToken cancellationToken = default);

        /// <summary>Fills the omitted execution and state limits of a frame transaction's frames by replaying it.</summary>
        /// <remarks>Runs in <paramref name="header"/>'s state with the overrides applied. Every probe runs the whole
        /// transaction; the filled limits are verified by a final run at the transaction's requested fees.</remarks>
        /// <param name="header">The block to estimate in; it is copied, never modified.</param>
        /// <param name="tx">The frame transaction; its frames are copied, never modified.</param>
        /// <param name="fillExecution">Per frame, indexed like <c>tx.Frames</c> and of the same length: whether to fill that
        /// frame's execution limit. Where false, the frame's limit is kept.</param>
        /// <param name="fillState">Per frame, indexed like <c>tx.Frames</c> and of the same length: whether to fill that
        /// frame's state limit. Where false, the frame's limit is kept.</param>
        /// <param name="gasCap">The most the frame limits plus signature verification work may add up to in any run, and
        /// the most the filled transaction's total gas budget may be.</param>
        /// <param name="errorMargin">In basis points: each limit's search stops once its bounds are within this fraction of
        /// the upper bound, which it returns. 0 searches exactly; it must be below 10000.</param>
        /// <param name="stateOverride">Account overrides applied before the estimate.</param>
        /// <param name="blockOverride">Block overrides applied to <paramref name="header"/>'s copy.</param>
        /// <param name="cancellationToken">Cancels the estimate between and within runs.</param>
        /// <param name="executionReverted"><see langword="true"/> only when the estimate fails because a frame of an
        /// otherwise valid transaction reverted, rather than ran out of gas or the transaction was invalid.</param>
        /// <returns>The frames with every requested limit filled, or the failure.</returns>
        Result<TxFrame[]> EstimateFrameGas(BlockHeader header, Transaction tx, bool[] fillExecution, bool[] fillState, ulong gasCap, int errorMargin,
            Dictionary<Address, AccountOverride>? stateOverride, BlockOverride? blockOverride, CancellationToken cancellationToken, out bool executionReverted);

        CallOutput CreateAccessList(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride>? stateOverride, bool optimize, UInt256? blobBaseFeeOverride = null, CancellationToken cancellationToken = default);
        ulong GetChainId();

        int NewBlockFilter();
        int NewPendingTransactionFilter();
        int NewFilter(BlockParameter fromBlock, BlockParameter toBlock, HashSet<AddressAsKey>? address = null, IEnumerable<Hash256[]?>? topics = null);
        void UninstallFilter(int filterId);
        bool FilterExists(int filterId);
        /// <returns>A pooled list that the caller disposes.</returns>
        ArrayPoolList<Hash256> GetBlockFilterChanges(int filterId);

        /// <returns>A pooled list that the caller disposes.</returns>
        ArrayPoolList<Hash256> GetPendingTransactionFilterChanges(int filterId);

        /// <returns>A pooled list that the caller disposes.</returns>
        ArrayPoolList<FilterLog> GetLogFilterChanges(int filterId);
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

        ReadOnlyBlockAccessList? GetBlockAccessList(ulong blockNumber, Hash256 blockHash);
        MemoryManager<byte>? GetBlockAccessListRlp(ulong blockNumber, Hash256 blockHash);
        void DeleteBlockAccessList(ulong blockNumber, Hash256 blockHash);
    }
}

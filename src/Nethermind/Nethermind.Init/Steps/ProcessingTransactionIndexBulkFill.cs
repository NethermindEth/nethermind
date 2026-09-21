// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.History.Changesets;

namespace Nethermind.Init.Steps;

public sealed class ProcessingTransactionIndexBulkFill(
    IBlockTree blocks,
    ISpecProvider specs,
    TransactionChangesetIndex index,
    BulkFillSessionFactory sessions,
    TransactionIndexGenesisBootstrap genesisBootstrap,
    IFlatDbConfig config,
    ILifetimeScope root,
    IBlockValidationModule[] validationModules,
    ILogManager logs) : ITransactionIndexBulkFill
{
    internal const ProcessingOptions ReplayOptions = ProcessingOptions.ForceProcessing | ProcessingOptions.ReadOnlyChain
        | ProcessingOptions.NoValidation | ProcessingOptions.ForceSequentialBlockAccessList;

    private readonly ILogger _logger = logs.GetClassLogger<ProcessingTransactionIndexBulkFill>();
    public bool Enabled => config.HistoryTransactionIndexBulkFillEnabled;

    public void Run(CancellationToken token)
    {
        if (!Enabled) return;
        if (specs.ChainId != 1 || config.HistoryTransactionIndexRetrofitFromBlock == 0)
        {
            if (_logger.IsError) _logger.Error("Bulk transaction indexing requires mainnet and a nonzero retrofit floor.");
            return;
        }
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (TryFill(token)) return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is InvalidBlockException or NotSupportedException or ScratchStateUnusableException)
            {
                if (_logger.IsError) _logger.Error("Bulk transaction index stopped until restart; checkpoint retained for diagnosis.", exception);
                return;
            }
            catch (Exception exception)
            {
                // Anything else is expected to clear on its own: a body still downloading, a source state still being
                // captured, a disk that fills and is freed.
                if (_logger.IsWarn) _logger.Warn($"Bulk transaction index paused with its checkpoint retained: {exception.Message}");
            }
            if (token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30))) return;
        }
    }

    private bool TryFill(CancellationToken token)
    {
        ulong first = config.HistoryTransactionIndexRetrofitFromBlock;
        if (!index.TryGetCoverage(out ulong coveredFrom, out _)) return false;
        BlockHeader anchor = FindHeader(first - 1);
        BlockHeader genesis = FindHeader(0);
        using BulkFillSession session = sessions.Open(genesis.Hash!, anchor);
        if (coveredFrom <= first)
        {
            index.SyncWal();
            session.ReleaseState(token);
            return true;
        }
        RequireCanonical(anchor);
        sessions.CheckDisk(session);
        if (!genesisBootstrap.TryImport(session, token))
            sessions.Import(session, anchor, () => RequireCanonical(anchor), token);
        RequireCanonical(anchor);
        BlockHeader checkpoint = FindHeader(session.CurrentState.BlockNumber);
        if (checkpoint.Hash != session.BlockHash || new StateId(checkpoint) != session.CurrentState)
            throw new ScratchStateUnusableException("Bulk replay checkpoint no longer matches the canonical chain.");
        sessions.ValidateSource(checkpoint);
        session.CleanStorage(token);
        BulkFillScopeProvider provider = new(session, root.Resolve<ITrieNodeCache>(), root.Resolve<IResourcePool>(), config, logs);
        using ILifetimeScope scope = root.BeginLifetimeScope(builder => builder
            .AddModule(validationModules)
            .AddSingleton<IWorldStateScopeProvider>(provider)
            .AddSingleton<IStateReader>(provider)
            .AddDecorator<IBlockchainProcessor, OneTimeChainProcessor>()
            .AddScoped<BlockchainProcessor.Options>(BlockchainProcessor.Options.NoReceipts));
        IBlockchainProcessor processor = scope.Resolve<IBlockchainProcessor>();
        long reportedAt = Stopwatch.GetTimestamp();
        ulong reportedBlock = session.CurrentState.BlockNumber;
        while (session.CurrentState.BlockNumber + 1 < coveredFrom)
        {
            token.ThrowIfCancellationRequested();
            sessions.CheckDisk(session);
            long startedAt = Stopwatch.GetTimestamp();
            Block block = blocks.FindBlock(session.CurrentState.BlockNumber + 1, BlockTreeLookupOptions.RequireCanonical)
                ?? throw new InvalidDataException("Bulk replay is waiting for a canonical block body.");
            if (specs.GetSpec(block.Header).BlockLevelAccessListsEnabled) throw new NotSupportedException("Bulk replay does not support BAL-enabled blocks.");
            sessions.ValidateSource(block.Header);
            Execute(block, checkpoint, session, processor, token);
            checkpoint = block.Header;
            session.CleanStorage(token);
            sessions.Rest(Stopwatch.GetElapsedTime(startedAt), token);
            if (Stopwatch.GetElapsedTime(reportedAt) >= TimeSpan.FromSeconds(30))
            {
                double rate = (session.CurrentState.BlockNumber - reportedBlock) / Stopwatch.GetElapsedTime(reportedAt).TotalSeconds;
                if (_logger.IsInfo) _logger.Info($"Bulk transaction index replay {session.CurrentState.BlockNumber}/{coveredFrom - 1}: {rate:F1} blocks/s, scratch SST/blob {session.Size / (1024 * 1024)} MiB.");
                reportedAt = Stopwatch.GetTimestamp();
                reportedBlock = session.CurrentState.BlockNumber;
            }
        }
        RequireCanonical(anchor);
        RequireCanonical(checkpoint);
        sessions.ValidateSource(anchor);
        index.SyncWal();
        if (!index.TryClaim(first, session.CurrentState.BlockNumber)) throw new InvalidOperationException("Bulk replay has not joined contiguous coverage.");
        index.SyncWal();
        session.ReleaseState(token);
        if (_logger.IsInfo) _logger.Info($"Bulk transaction index completed {first}-{checkpoint.Number}; scratch rows released, remaining files {session.Size / (1024 * 1024)} MiB pending compaction.");
        return true;
    }

    private void Execute(Block block, BlockHeader parent, BulkFillSession session, IBlockchainProcessor processor, CancellationToken token)
    {
        RequireCanonical(parent);
        RequireCanonical(block.Header);
        session.BeginBlock(block.Header);
        using TransactionChangesetIndex.BlockCapture capture = index.StartBlock(block.Number);
        Block isolated = block.WithReplacedHeader(block.Header.Clone());
        try
        {
            if (processor.Process(isolated, ReplayOptions, capture.Tracer, token) is null)
                throw new InvalidBlockException(block, $"Bulk replay failed at {block.Number}.");
            token.ThrowIfCancellationRequested();
            RequireCanonical(block.Header);
            sessions.ValidateSource(block.Header);
            if (!capture.Commit()) throw new InvalidDataException($"Bulk replay capture incomplete at {block.Number}.");
            index.SyncWal();
            session.CommitBlock();
        }
        finally
        {
            isolated.DisposeAccountChanges();
        }
    }

    private BlockHeader FindHeader(ulong number) => blocks.FindHeader(number, BlockTreeLookupOptions.RequireCanonical)
        ?? throw new InvalidDataException($"Bulk replay needs canonical header {number}.");

    private void RequireCanonical(BlockHeader header)
    {
        if (FindHeader(header.Number).Hash != header.Hash) throw new InvalidDataException($"Bulk replay canonical anchor changed at {header.Number}.");
    }
}

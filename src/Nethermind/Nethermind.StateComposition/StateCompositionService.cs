// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.StateComposition;

/// <summary>
/// Orchestrates state composition analysis using <see cref="StateCompositionVisitor"/>
/// and <see cref="IStateReader"/> for trie traversal.
/// </summary>
public sealed class StateCompositionService : IStateCompositionService
{
    private readonly IStateReader _stateReader;
    private readonly IStateCompositionStateHolder _stateHolder;
    private readonly IStateCompositionConfig _config;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    private volatile CancellationTokenSource? _currentScanCts;
    private long _lastScanCompletedTicks;

    public StateCompositionService(
        IStateReader stateReader,
        IStateCompositionStateHolder stateHolder,
        IStateCompositionConfig config,
        ILogManager logManager)
    {
        _stateReader = stateReader;
        _stateHolder = stateHolder;
        _config = config;
        _logManager = logManager;
        _logger = logManager.GetClassLogger();

        ValidateConfig(config);
    }

    private static void ValidateConfig(IStateCompositionConfig config)
    {
        if (config.ScanParallelism <= 0)
            throw new ArgumentException("ScanParallelism must be positive", nameof(config));
        if (config.ScanMemoryBudget <= 0)
            throw new ArgumentException("ScanMemoryBudget must be positive", nameof(config));
        if (config.ScanQueueTimeoutSeconds <= 0)
            throw new ArgumentException("ScanQueueTimeoutSeconds must be positive", nameof(config));
        if (config.TopNContracts <= 0)
            throw new ArgumentException("TopNContracts must be positive", nameof(config));
        if (config.ScanCooldownSeconds < 0)
            throw new ArgumentException("ScanCooldownSeconds must be non-negative", nameof(config));
    }

    public async Task<StateCompositionStats> AnalyzeAsync(BlockHeader header, CancellationToken ct)
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastScanCompletedTicks);
        long cooldownMs = _config.ScanCooldownSeconds * 1000L;
        if (last > 0 && now - last < cooldownMs)
        {
            long remainingSeconds = (cooldownMs - (now - last)) / 1000;
            throw new InvalidOperationException(
                $"Scan cooldown active. Try again in {remainingSeconds} seconds.");
        }

        bool acquired = false;
        try
        {
            acquired = await _scanLock.WaitAsync(
                TimeSpan.FromSeconds(_config.ScanQueueTimeoutSeconds), ct).ConfigureAwait(false);

            if (!acquired)
                throw new InvalidOperationException(
                    "Scan already in progress. Use statecomp_getCachedStats() for last results.");

            _stateHolder.MarkScanStarted();
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _currentScanCts = linkedCts;

            Stopwatch sw = Stopwatch.StartNew();

            if (_logger.IsInfo)
                _logger.Info($"StateComposition: starting full scan at block {header.Number}, root {header.StateRoot}");

            using StateCompositionVisitor visitor = new(
                _logManager, linkedCts.Token, _config.TopNContracts, _config.ExcludeStorage);

            VisitingOptions options = new()
            {
                MaxDegreeOfParallelism = _config.ScanParallelism,
                FullScanMemoryBudget = _config.ScanMemoryBudget,
            };

            PeriodicTimer progressTimer = new(TimeSpan.FromSeconds(8));
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await progressTimer.WaitForNextTickAsync(CancellationToken.None).ConfigureAwait(false))
                    {
                        if (_logger.IsInfo)
                            _logger.Info($"StateComposition: scan in progress, elapsed {sw.Elapsed}");
                    }
                }
                catch (OperationCanceledException) { }
            }, CancellationToken.None);

            try
            {
                await Task.Run(() =>
                    _stateReader.RunTreeVisitor(visitor, header, options), linkedCts.Token).ConfigureAwait(false);
            }
            finally
            {
                progressTimer.Dispose();
            }

            StateCompositionStats stats = visitor.GetStats(header.Number, header.StateRoot);
            TrieDepthDistribution dist = visitor.GetTrieDistribution();

            _stateHolder.SetBaseline(stats, dist);
            _stateHolder.MarkScanCompleted(header.Number, header.StateRoot!, sw.Elapsed);

            Interlocked.Exchange(ref _lastScanCompletedTicks, Environment.TickCount64);

            if (_logger.IsInfo)
                _logger.Info($"StateComposition: scan completed in {sw.Elapsed}. " +
                             $"Accounts={stats.AccountsTotal}, Contracts={stats.ContractsTotal}, " +
                             $"StorageSlots={stats.StorageSlotsTotal}");

            return stats;
        }
        finally
        {
            _currentScanCts = null;
            if (acquired)
                _scanLock.Release();
        }
    }

    public Task<TrieDepthDistribution> GetTrieDistributionAsync(BlockHeader header, CancellationToken ct)
    {
        if (_stateHolder.IsInitialized)
            return Task.FromResult(_stateHolder.CurrentDistribution);

        throw new InvalidOperationException(
            "No cached distribution available. Run statecomp_getStats() first to trigger a scan.");
    }

    public async Task<TopContractEntry?> InspectContractAsync(Address address, BlockHeader header, CancellationToken ct)
    {
        if (!_stateReader.TryGetAccount(header, address, out AccountStruct account))
            return null;

        if (!account.HasStorage)
            return null;

        ValueHash256 accountHash = ValueKeccak.Compute(address.Bytes);
        ValueHash256 targetStorageRoot = account.StorageRoot;

        if (_logger.IsInfo)
            _logger.Info($"StateComposition: inspecting contract {address}, storageRoot={targetStorageRoot}");

        using SingleContractVisitor visitor = new(_logManager, ct, targetStorageRoot);

        VisitingOptions options = new()
        {
            MaxDegreeOfParallelism = 1,
            FullScanMemoryBudget = _config.ScanMemoryBudget,
        };

        await Task.Run(() =>
            _stateReader.RunTreeVisitor(visitor, header, options), ct).ConfigureAwait(false);

        return visitor.GetResult(accountHash, targetStorageRoot);
    }

    public void CancelScan()
    {
        // The CTS may be disposed between our read and Cancel() call
        // if the scan completes concurrently — catch and ignore.
        try
        {
            _currentScanCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Scan already completed and disposed the CTS
        }
    }

    /// <summary>
    /// Specialized visitor that walks the full state trie but only collects storage statistics
    /// for a single target contract identified by its storage root.
    /// Skips all non-target storage tries for efficiency.
    /// </summary>
    private sealed class SingleContractVisitor : ITreeVisitor<OldStyleTrieVisitContext>, IDisposable
    {
        private readonly ILogger _logger;
        private readonly CancellationToken _ct;
        private readonly ValueHash256 _targetStorageRoot;

        private bool _collectingTarget;
        private bool _targetCompleted;

        private int _maxDepth;
        private long _totalNodes;
        private long _valueNodes;
        private long _totalSize;
        private readonly DepthCounter[] _depths = new DepthCounter[VisitorCounters.MaxTrackedDepth];

        public bool IsFullDbScan => true;
        public ReadFlags ExtraReadFlag => ReadFlags.HintCacheMiss;
        public bool ExpectAccounts => true;

        public SingleContractVisitor(ILogManager logManager, CancellationToken ct, ValueHash256 targetStorageRoot)
        {
            _logger = logManager.GetClassLogger();
            _ct = ct;
            _targetStorageRoot = targetStorageRoot;
        }

        public bool ShouldVisit(in OldStyleTrieVisitContext ctx, in ValueHash256 nextNode)
        {
            if (_ct.IsCancellationRequested) return false;
            if (_targetCompleted) return false;
            if (ctx.IsStorage && !_collectingTarget) return false;
            return true;
        }

        public void VisitTree(in OldStyleTrieVisitContext ctx, in ValueHash256 rootHash) { }

        public void VisitMissingNode(in OldStyleTrieVisitContext ctx, in ValueHash256 nodeHash)
        {
            if (_logger.IsWarn)
                _logger.Warn($"InspectContract: missing node at depth {ctx.Level}");
        }

        public void VisitBranch(in OldStyleTrieVisitContext ctx, TrieNode node)
        {
            if (!ctx.IsStorage || !_collectingTarget) return;
            int byteSize = node.FullRlp.Length;
            int depth = Math.Min(ctx.Level, VisitorCounters.MaxTrackedDepth - 1);
            _totalNodes++;
            _totalSize += byteSize;
            if (ctx.Level > _maxDepth) _maxDepth = ctx.Level;
            _depths[depth].AddFullNode(byteSize);
        }

        public void VisitExtension(in OldStyleTrieVisitContext ctx, TrieNode node)
        {
            if (!ctx.IsStorage || !_collectingTarget) return;
            int byteSize = node.FullRlp.Length;
            int depth = Math.Min(ctx.Level, VisitorCounters.MaxTrackedDepth - 1);
            _totalNodes++;
            _totalSize += byteSize;
            if (ctx.Level > _maxDepth) _maxDepth = ctx.Level;
            _depths[depth].AddShortNode(byteSize);
        }

        public void VisitLeaf(in OldStyleTrieVisitContext ctx, TrieNode node)
        {
            if (!ctx.IsStorage || !_collectingTarget) return;
            int byteSize = node.FullRlp.Length;
            int depth = Math.Min(ctx.Level, VisitorCounters.MaxTrackedDepth - 1);
            _totalNodes++;
            _valueNodes++;
            _totalSize += byteSize;
            if (ctx.Level > _maxDepth) _maxDepth = ctx.Level;
            _depths[depth].AddValueNode(byteSize);
        }

        public void VisitAccount(in OldStyleTrieVisitContext ctx, TrieNode node, in AccountStruct account)
        {
            if (_collectingTarget)
            {
                _collectingTarget = false;
                _targetCompleted = true;
                return;
            }

            if (!_targetCompleted && account.HasStorage && account.StorageRoot == _targetStorageRoot)
            {
                _collectingTarget = true;
            }
        }

        public TopContractEntry? GetResult(ValueHash256 owner, ValueHash256 storageRoot)
        {
            if (!_collectingTarget && !_targetCompleted)
                return null;

            ImmutableArray<TrieLevelStat>.Builder levelsBuilder =
                ImmutableArray.CreateBuilder<TrieLevelStat>(VisitorCounters.MaxTrackedDepth);
            long summaryShort = 0, summaryFull = 0, summaryValue = 0, summarySize = 0;

            for (int i = 0; i < VisitorCounters.MaxTrackedDepth; i++)
            {
                ref DepthCounter dc = ref _depths[i];
                levelsBuilder.Add(new TrieLevelStat
                {
                    Depth = i,
                    ShortNodeCount = dc.ShortNodes,
                    FullNodeCount = dc.FullNodes,
                    ValueNodeCount = dc.ValueNodes,
                    TotalSize = dc.TotalSize,
                });
                summaryShort += dc.ShortNodes;
                summaryFull += dc.FullNodes;
                summaryValue += dc.ValueNodes;
                summarySize += dc.TotalSize;
            }

            return new TopContractEntry
            {
                Owner = owner,
                StorageRoot = storageRoot,
                MaxDepth = _maxDepth,
                TotalNodes = _totalNodes,
                ValueNodes = _valueNodes,
                TotalSize = _totalSize,
                Levels = levelsBuilder.MoveToImmutable(),
                Summary = new TrieLevelStat
                {
                    Depth = -1,
                    ShortNodeCount = summaryShort,
                    FullNodeCount = summaryFull,
                    ValueNodeCount = summaryValue,
                    TotalSize = summarySize,
                },
            };
        }

        public void Dispose() { }
    }
}

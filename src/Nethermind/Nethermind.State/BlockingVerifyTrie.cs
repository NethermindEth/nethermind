// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class BlockingVerifyTrie
{
    private readonly ILogger _logger;
    private readonly ITrieStore _trieStore;
    private readonly IStateReader _stateReader;
    private readonly IDb _codeDb;
    private readonly ILogManager _logManager;
    private readonly ProgressLogger _progressLogger;

    public BlockingVerifyTrie(
        ITrieStore trieStore,
        IStateReader stateReader,
        [KeyFilter(DbNames.Code)] IDb codeDb,
        ILogManager logManager)
    {
        if (trieStore is IReadOnlyTrieStore)
        {
            throw new InvalidOperationException("TrieStore must not be read only to be able to block processing.");
        }

        _trieStore = trieStore;
        _stateReader = stateReader;
        _codeDb = codeDb;
        _logManager = logManager;
        _logger = logManager.GetClassLogger<BlockingVerifyTrie>();
        _progressLogger = new ProgressLogger("Trie Verification", logManager);
    }

    public bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken)
    {
        // This is to block processing as with halfpath old nodes will be removed
        using IDisposable _ = _trieStore.BeginScope(stateAtBlock);

        Hash256 rootNode = stateAtBlock.StateRoot;

        if (_logger.IsInfo) _logger.Info($"Starting trie verification for block {stateAtBlock.Number} with state root {rootNode}");

        // Initialize progress logger
        _progressLogger.Reset(0, 0); // We'll update as we go since we don't know total nodes upfront

        TrieStats stats = _stateReader.CollectStats(rootNode, _codeDb, _logManager, _progressLogger, cancellationToken);

        // Update progress logger with final stats
        _progressLogger.Update(stats.NodesCount);
        _progressLogger.MarkEnd();
        _progressLogger.LogProgress();

        if (stats.MissingNodes > 0)
        {
            if (_logger.IsError) _logger.Error($"Missing node found!");
        }

        if (_logger.IsInfo) _logger.Info($"Stats after finishing state \n" + stats);

        return stats.MissingNodes == 0;
    }
}

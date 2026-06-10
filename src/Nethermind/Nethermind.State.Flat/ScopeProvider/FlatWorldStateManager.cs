// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.State.SnapServer;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.ScopeProvider;

public class FlatWorldStateManager : IWorldStateManager
{
    private readonly IFlatDbManager _flatDbManager;
    private readonly IPersistenceManager _persistenceManager;
    private readonly IFlatDbConfig _configuration;
    private readonly FlatStateReader _flatStateReader;
    private readonly Func<FlatOverridableWorldScope> _overridableWorldScopeFactory;
    private readonly IDb _codeDb;
    private readonly IFlatStateRootIndex _flatStateRootIndex;
    private readonly ILogManager _logManager;

    // Shared across the main and resettable scope providers so its dedicated reader threads
    // are spawned once and reused for every block's BAL warm-read pump.
    private readonly BalReaderPool? _balReaderPool;

    private readonly FlatScopeProvider _mainWorldState;
    private readonly FlatTrieVerifier _trieVerifier;

    private FlatSnapServer? _snapServer;

    public FlatWorldStateManager(
        IFlatDbManager flatDbManager,
        IPersistence persistence,
        IPersistenceManager persistenceManager,
        IFlatDbConfig configuration,
        FlatStateReader flatStateReader,
        ITrieWarmer trieWarmer,
        Func<FlatOverridableWorldScope> overridableWorldScopeFactory,
        [KeyFilter(DbNames.Code)] IDb codeDb,
        IFlatStateRootIndex flatStateRootIndex,
        ILogManager logManager)
    {
        _flatDbManager = flatDbManager;
        _persistenceManager = persistenceManager;
        _configuration = configuration;
        _flatStateReader = flatStateReader;
        _overridableWorldScopeFactory = overridableWorldScopeFactory;
        _codeDb = codeDb;
        _flatStateRootIndex = flatStateRootIndex;
        _logManager = logManager;

        _balReaderPool = ResolveBalReaderPool(configuration);

        _mainWorldState = new FlatScopeProvider(
            codeDb,
            flatDbManager,
            configuration,
            trieWarmer,
            _balReaderPool,
            ResourcePool.Usage.MainBlockProcessing,
            logManager,
            isReadOnly: false);

        _trieVerifier = new FlatTrieVerifier(flatDbManager, persistence, logManager);
    }

    public IWorldStateScopeProvider GlobalWorldState => _mainWorldState;
    public IStateReader GlobalStateReader => _flatStateReader;
    public ISnapServer SnapServer => _snapServer ??= new FlatSnapServer(
        _flatDbManager,
        _codeDb,
        _flatStateRootIndex,
        _logManager);
    public IReadOnlyKeyValueStore? HashServer => null;

    public long? RetentionWindowBlocks => null;

    public long? OldestStateBlock
    {
        get
        {
            long blockNumber = _persistenceManager.GetCurrentPersistedStateId().BlockNumber;
            return blockNumber >= 0 ? blockNumber : null;
        }
    }

    public IWorldStateScopeProvider CreateResettableWorldState() =>
        new FlatScopeProvider(
            _codeDb,
            _flatDbManager,
            _configuration,
            new NoopTrieWarmer(),
            _balReaderPool,
            ResourcePool.Usage.ReadOnlyProcessingEnv,
            _logManager,
            isReadOnly: true);

    event EventHandler<ReorgBoundaryReached>? IWorldStateManager.ReorgBoundaryReached
    {
        add => _flatDbManager.ReorgBoundaryReached += value;
        remove => _flatDbManager.ReorgBoundaryReached -= value;
    }

    public IReadOnlyTrieStore CreateReadOnlyTrieStore() => new FlatReadOnlyTrieStore(_flatDbManager);

    public IOverridableWorldScope CreateOverridableWorldScope() =>
        _overridableWorldScopeFactory();

    public bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken) =>
        _trieVerifier.Verify(stateAtBlock, cancellationToken);

    public void FlushCache(CancellationToken cancellationToken) => _flatDbManager.FlushCache(cancellationToken);

    private static BalReaderPool? ResolveBalReaderPool(IFlatDbConfig config)
    {
        int configured = config.WarmReadConcurrency;
        int concurrency = configured < 0 ? Math.Min(4 * Environment.ProcessorCount, 64) : configured;
        return concurrency >= 1 ? new BalReaderPool(concurrency) : null;
    }
}

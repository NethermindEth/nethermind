// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Channels;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt;

public class PbtDbManager : IPbtDbManager, IAsyncDisposable
{
    private const int GatherRetryLimit = 16;

    private readonly PbtSnapshotRepository _repository;
    private readonly PbtPersistenceCoordinator _coordinator;
    private readonly IPbtPersistence _persistence;
    private readonly IPbtResourcePool _resourcePool;
    private readonly ILogger _logger;
    private readonly Channel<bool> _workSignal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Task _persistenceWorker;
    private readonly CancellationToken _exitToken;

    public PbtDbManager(
        PbtSnapshotRepository repository,
        PbtPersistenceCoordinator coordinator,
        IPbtPersistence persistence,
        IPbtResourcePool resourcePool,
        IProcessExitSource processExitSource,
        ILogManager logManager)
    {
        _repository = repository;
        _coordinator = coordinator;
        _persistence = persistence;
        _resourcePool = resourcePool;
        _logger = logManager.GetClassLogger<PbtDbManager>();
        _exitToken = processExitSource.Token;
        _persistenceWorker = Task.Run(RunPersistenceWorker);
    }

    public PbtReadOnlySnapshotBundle? TryGatherReadOnlyBundle(in StateId stateId)
    {
        // the pre-genesis state is empty by definition, whatever is on disk
        if (stateId == StateId.PreGenesis) return new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), EmptyPersistenceReader.Instance);

        // reader first, chain second: if persistence advances in between, the chain walk to the
        // reader's (stale) floor fails and we retry with a fresh reader; leased layers pruned
        // after assembly stay readable through their leases
        for (int attempt = 0; attempt < GatherRetryLimit; attempt++)
        {
            IPbtPersistence.IReader reader = _persistence.CreateReader();
            PbtSnapshotPooledList chain = new(1);
            if (_repository.TryLeaseChain(stateId, reader.CurrentState, chain))
            {
                // ownership of the chain and the reader passes to the bundle
                return new PbtReadOnlySnapshotBundle(chain, reader);
            }

            // a broken walk leaves the partial leases on the chain: disposing it releases them
            chain.Dispose();
            reader.Dispose();
        }

        return null;
    }

    public PbtSnapshotBundle? TryGatherBundle(in StateId stateId, PbtResourcePool.Usage usage)
    {
        if (TryGatherReadOnlyBundle(stateId) is not { } readOnlyBundle) return null;

        try
        {
            // ownership of the shared bundle's lease passes to the writable one
            return new PbtSnapshotBundle(new PbtSnapshotPooledList(1), readOnlyBundle, _resourcePool, usage);
        }
        catch
        {
            readOnlyBundle.Dispose();
            throw;
        }
    }

    public void AddSnapshot(PbtSnapshot snapshot)
    {
        _repository.TryAdd(snapshot);
        _workSignal.Writer.TryWrite(true);
    }

    public bool HasStateForBlock(in StateId stateId) =>
        stateId == StateId.PreGenesis
        || _repository.HasState(stateId)
        || _coordinator.GetCurrentPersistedStateId() == stateId;

    public void FlushCache(CancellationToken cancellationToken) => _coordinator.FlushToPersistence();

    private async Task RunPersistenceWorker()
    {
        try
        {
            await foreach (bool _ in _workSignal.Reader.ReadAllAsync(_exitToken))
            {
                try
                {
                    _coordinator.CheckPersistence();
                }
                catch (Exception e)
                {
                    if (_logger.IsError) _logger.Error("Pbt persistence failed", e);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _workSignal.Writer.TryComplete();
        await _persistenceWorker;
    }

    private sealed class EmptyPersistenceReader : IPbtPersistence.IReader
    {
        public static readonly EmptyPersistenceReader Instance = new();

        private EmptyPersistenceReader()
        {
        }

        public StateId CurrentState => StateId.PreGenesis;
        public Account? GetAccount(Address address) => null;
        public EvmWord GetSlot(Address address, in UInt256 slot) => default;
        public RefCountingMemory? GetLeafBlob(in Stem stem) => null;
        public RefCountingMemory? GetTrieNode(in TrieNodeKey key) => null;

        public void Dispose()
        {
        }
    }
}

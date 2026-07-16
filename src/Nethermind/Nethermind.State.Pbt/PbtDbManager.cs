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
    private readonly ILogger _logger;
    private readonly Channel<bool> _workSignal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Task _persistenceWorker;
    private readonly CancellationToken _exitToken;

    public PbtDbManager(
        PbtSnapshotRepository repository,
        PbtPersistenceCoordinator coordinator,
        IPbtPersistence persistence,
        IProcessExitSource processExitSource,
        ILogManager logManager)
    {
        _repository = repository;
        _coordinator = coordinator;
        _persistence = persistence;
        _logger = logManager.GetClassLogger<PbtDbManager>();
        _exitToken = processExitSource.Token;
        _persistenceWorker = Task.Run(RunPersistenceWorker);
    }

    public PbtSnapshotBundle? TryGatherBundle(in StateId stateId, bool isReadOnly)
    {
        // the pre-genesis state is empty by definition, whatever is on disk
        if (stateId == StateId.PreGenesis) return new PbtSnapshotBundle([], EmptyPersistenceReader.Instance, isReadOnly);

        // reader first, chain second: if persistence advances in between, the chain walk to the
        // reader's (stale) floor fails and we retry with a fresh reader; leased layers pruned
        // after assembly stay readable through their leases
        for (int attempt = 0; attempt < GatherRetryLimit; attempt++)
        {
            IPbtPersistence.IReader reader = _persistence.CreateReader();
            List<PbtSnapshot> chain = [];
            if (_repository.TryLeaseChain(stateId, reader.CurrentState, chain))
            {
                return new PbtSnapshotBundle(chain, reader, isReadOnly);
            }

            reader.Dispose();
        }

        return null;
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

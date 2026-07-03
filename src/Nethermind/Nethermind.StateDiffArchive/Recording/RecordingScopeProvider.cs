// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.StateDiffArchive.Data;
using Nethermind.StateDiffArchive.Storage;

namespace Nethermind.StateDiffArchive.Recording;

/// <summary>
/// Decorates the main <see cref="IWorldStateScopeProvider"/> so that every committed block's net
/// account/storage/code writes are teed into a <see cref="StateDiffRecord"/> and persisted via
/// <see cref="StateDiffStore"/>. The writes themselves still flow through to the real scope unchanged.
/// </summary>
public sealed class RecordingScopeProvider(
    IWorldStateScopeProvider inner,
    StateDiffStore store,
    ILogManager logManager) : IWorldStateScopeProvider
{
    private readonly ILogger _logger = logManager.GetClassLogger<RecordingScopeProvider>();

    public bool HasRoot(BlockHeader? baseBlock) => inner.HasRoot(baseBlock);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock, LocalMetrics metrics)
        => new RecordingScope(inner.BeginScope(baseBlock, metrics), store, _logger);

    private sealed class RecordingScope(
        IWorldStateScopeProvider.IScope inner,
        StateDiffStore store,
        ILogger logger) : IWorldStateScopeProvider.IScope
    {
        private readonly StateDiffRecordBuilder _builder = new();
        private RecordingCodeDb? _codeDb;

        public Hash256 RootHash => inner.RootHash;
        public void UpdateRootHash() => inner.UpdateRootHash();
        public Account? Get(Address address) => inner.Get(address);
        public void HintGet(Address address, Account? account) => inner.HintGet(address, account);
        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => inner.CreateStorageTree(address);
        public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null) => inner.HintBal(bal, sink);

        public IWorldStateScopeProvider.ICodeDb CodeDb => _codeDb ??= new RecordingCodeDb(inner.CodeDb, _builder);

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
            => new RecordingWriteBatch(inner.StartWriteBatch(estimatedAccountNum), _builder.StartBatch());

        public void Commit(ulong blockNumber)
        {
            inner.Commit(blockNumber);
            try
            {
                store.Write(_builder, blockNumber, RootHash);
                Metrics.LastRecordedBlock = (long)blockNumber;
                Metrics.BlocksRecorded++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (logger.IsError) logger.Error($"StateDiffArchive: failed to record state diff for block {blockNumber}", ex);
            }
            finally
            {
                _builder.Reset();
            }
        }

        public void Dispose() => inner.Dispose();
    }

    private sealed class RecordingWriteBatch(
        IWorldStateScopeProvider.IWorldStateWriteBatch inner,
        StateDiffRecordBuilder.BatchBuilder batch) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated
        {
            add => inner.OnAccountUpdated += value;
            remove => inner.OnAccountUpdated -= value;
        }

        public void Set(Address key, Account? account)
        {
            batch.SetAccount(key, account);
            inner.Set(key, account);
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries)
            => new RecordingStorageWriteBatch(inner.CreateStorageWriteBatch(key, estimatedEntries), batch, key);

        public void Dispose() => inner.Dispose();
    }

    private sealed class RecordingStorageWriteBatch(
        IWorldStateScopeProvider.IStorageWriteBatch inner,
        StateDiffRecordBuilder.BatchBuilder batch,
        Address address) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public void Set(in UInt256 index, byte[] value)
        {
            batch.SetSlot(address, index, value);
            inner.Set(index, value);
        }

        public void Clear()
        {
            batch.ClearStorage(address);
            inner.Clear();
        }

        public void Dispose() => inner.Dispose();
    }

    private sealed class RecordingCodeDb(
        IWorldStateScopeProvider.ICodeDb inner,
        StateDiffRecordBuilder builder) : IWorldStateScopeProvider.ICodeDb
    {
        public byte[]? GetCode(in ValueHash256 codeHash) => inner.GetCode(in codeHash);
        public bool ContainsCode(in ValueHash256 codeHash) => inner.ContainsCode(in codeHash);
        public void MarkCodePersisted(in ValueHash256 codeHash) => inner.MarkCodePersisted(in codeHash);
        public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() => new RecordingCodeSetter(inner.BeginCodeWrite(), builder);
    }

    private sealed class RecordingCodeSetter(
        IWorldStateScopeProvider.ICodeSetter inner,
        StateDiffRecordBuilder builder) : IWorldStateScopeProvider.ICodeSetter
    {
        public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code)
        {
            builder.AddCode(codeHash, code.ToArray());
            inner.Set(in codeHash, code);
        }

        public void Dispose() => inner.Dispose();
    }
}

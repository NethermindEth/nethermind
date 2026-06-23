// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Synchronization.SnapSync;

internal sealed class NodeStoragePersistedCheckScope(INodeStorage nodeStorage) : IDisposable
{
    private readonly INodeStorage _nodeStorage = nodeStorage;
    private INodeStorageReadSnapshot? _snapshot;

    public IReadOnlyNodeStorage Current => _snapshot is not null ? _snapshot : _nodeStorage;

    public IDisposable Begin()
    {
        if (_snapshot is not null ||
            _nodeStorage is not INodeStorageWithReadSnapshot snapshotSource ||
            snapshotSource.CreateReadSnapshot() is not { } snapshot)
        {
            return EmptyPersistedCheckScope.Instance;
        }

        _snapshot = snapshot;
        return new Scope(this, snapshot);
    }

    public void Dispose()
    {
        _snapshot?.Dispose();
        _snapshot = null;
    }

    private sealed class Scope(NodeStoragePersistedCheckScope owner, INodeStorageReadSnapshot snapshot) : IDisposable
    {
        private readonly NodeStoragePersistedCheckScope _owner = owner;
        private readonly INodeStorageReadSnapshot _snapshot = snapshot;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (ReferenceEquals(_owner._snapshot, _snapshot))
            {
                _owner._snapshot = null;
                _snapshot.Dispose();
            }
        }
    }
}

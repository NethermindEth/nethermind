// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Core.Collections;

namespace Nethermind.State.Snap
{
    public class SlotsAndProofs : IDisposable
    {
        private IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> _pathsAndSlots =
            IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>>.Empty;

        private IByteArrayList _proofs = EmptyByteArrayList.Instance;

        [AllowNull]
        public IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> PathsAndSlots
        {
            get => _pathsAndSlots;
            set => _pathsAndSlots = value ?? IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>>.Empty;
        }

        [AllowNull]
        public IByteArrayList Proofs
        {
            get => _proofs;
            set => _proofs = value ?? EmptyByteArrayList.Instance;
        }

        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            PathsAndSlots.DisposeRecursive();
            Proofs.Dispose();
        }
    }
}

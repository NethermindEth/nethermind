// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Collections;

namespace Nethermind.State.Snap
{
    public class SlotsAndProofs : IDisposable
    {
        public IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> PathsAndSlots { get; set; }

        public IByteArrayList Proofs { get; set; }

        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            PathsAndSlots?.DisposeRecursive();
            Proofs?.Dispose();
        }
    }
}

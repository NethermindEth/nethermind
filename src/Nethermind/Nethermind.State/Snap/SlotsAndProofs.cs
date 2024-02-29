// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;

namespace Nethermind.State.Snap
{
    public class SlotsAndProofs : IDisposable
    {
        public IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> PathsAndSlots { get; set; }
        public IOwnedReadOnlyList<byte[]> Proofs { get; set; }

        private bool _disposed = false;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (PathsAndSlots != null)
            {
                foreach (IOwnedReadOnlyList<PathWithStorageSlot> pathWithStorageSlots in PathsAndSlots)
                {
                    pathWithStorageSlots?.Dispose();
                }
                PathsAndSlots?.Dispose();
            }
            Proofs?.Dispose();
        }
    }
}

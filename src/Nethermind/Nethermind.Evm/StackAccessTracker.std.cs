// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm;

public partial struct StackAccessTracker
{
    private sealed partial class TrackingState
    {
        // Rented once per top-level execution, not per frame, so one slot per thread is the whole win;
        // the collections a returned state keeps sized would otherwise be retained a slot at a time.
        private static readonly EvmObjectPool<TrackingState> _trackerPool = new(localCapacity: 1);
    }
}

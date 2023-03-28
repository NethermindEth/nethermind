// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.Plugin.GC;

class NoGCStrategy : IGCStrategy
{
    public static readonly NoGCStrategy Instance = new();

    public bool CanStartNoGCRegion() => false;
    public (GcLevel Generation, GcCompaction Compacting) GetForcedGCParams() => (GcLevel.NoGC, GcCompaction.No);
}

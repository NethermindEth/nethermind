// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.Plugin.GC;

public interface IGCStrategy
{
    bool CanStartNoGCRegion();
    (GcLevel Generation, GcCompaction Compacting) GetForcedGCParams();
}

public enum GcLevel
{
    NoGC = -1,
    Gen0 = 0,
    Gen1 = 1,
    Gen2 = 2
}

public enum GcCompaction
{
    No,
    Yes,
    Full
}

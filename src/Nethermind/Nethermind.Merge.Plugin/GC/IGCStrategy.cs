// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.Plugin.GC;

public interface IGCStrategy
{
    public const int NoGC = -1;
    public const int Gen0 = 0;
    public const int Gen1 = 1;
    public const int Gen2 = 2;

    public const int NoCompacting = 0;
    public const int Compacting = 1;
    public const int LOHCompacting = 2;

    bool CanStartNoGCRegion();
    (int Generation, int Compacting) GetForcedGCParams();
}

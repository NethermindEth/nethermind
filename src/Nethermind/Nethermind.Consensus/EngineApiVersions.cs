// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus;

public static class EngineApiVersions
{
    public const int Paris = 1;
    public const int Shanghai = 2;
    public const int Cancun = 3;
    public const int Prague = 4;
    public const int Osaka = 5;
    public const int Amsterdam = 6;

    /// <summary>
    /// forkchoiceUpdated method versions.
    /// </summary>
    /// <remarks>Multiple forks may share the same FCU version (e.g. Cancun/Prague/Osaka all use FCUv3).
    /// </remarks>
    public static class Fcu
    {
        public const int V1 = 1; // Paris
        public const int V2 = 2; // Shanghai
        public const int V3 = 3; // Cancun/Prague/Osaka
        public const int V4 = 4; // Amsterdam
    }

    /// <summary>
    /// Maps a fork's engine API version to the forkchoiceUpdated method version it uses.
    /// </summary>
    public static int FcuVersion(int apiVersion) => apiVersion switch
    {
        >= Amsterdam => Fcu.V4,  // Amsterdam
        >= Cancun => Fcu.V3, // Cancun/Prague/Osaka
        >= Shanghai => Fcu.V2, // Shanghai
        _ => Fcu.V1 // Paris
    };
}

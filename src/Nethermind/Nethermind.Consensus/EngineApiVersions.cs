// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus;

/// <summary>
/// Fork ordinals for ordering and range checks. These are NOT method version numbers —
/// use the nested classes (<see cref="Fcu"/>, <see cref="NewPayload"/>, <see cref="GetPayload"/>)
/// for actual method versions.
/// </summary>
public static class EngineApiVersions
{
    /// <summary>forkchoiceUpdated method versions.</summary>
    /// <remarks>Multiple forks may share the same version (e.g. Cancun/Prague/Osaka all use V3).</remarks>
    public static class Fcu
    {
        public const int V1 = 1; // Paris
        public const int V2 = 2; // Shanghai
        public const int V3 = 3; // Cancun/Prague/Osaka
        public const int V4 = 4; // Amsterdam
    }

    /// <summary>engine_newPayload method versions.</summary>
    public static class NewPayload
    {
        public const int V1 = 1; // Paris
        public const int V2 = 2; // Shanghai
        public const int V3 = 3; // Cancun
        public const int V4 = 4; // Prague/Osaka
        public const int V5 = 5; // Amsterdam
    }

    /// <summary>engine_getPayload method versions.</summary>
    public static class GetPayload
    {
        public const int V1 = 1; // Paris
        public const int V2 = 2; // Shanghai
        public const int V3 = 3; // Cancun
        public const int V4 = 4; // Prague
        public const int V5 = 5; // Osaka
        public const int V6 = 6; // Amsterdam
    }
}

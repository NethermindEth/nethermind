// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus;

/// <summary>
/// Engine API method version constants, grouped by method.
/// Use the nested classes (<see cref="Fcu"/>, <see cref="NewPayload"/>, <see cref="GetPayload"/>)
/// to select the appropriate version when calling Execution Engine API methods.
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
        public const int Latest = V4;
    }

    /// <summary>engine_newPayload method versions.</summary>
    public static class NewPayload
    {
        public const int V1 = 1; // Paris
        public const int V2 = 2; // Shanghai
        public const int V3 = 3; // Cancun
        public const int V4 = 4; // Prague/Osaka
        public const int V5 = 5; // Amsterdam
        public const int Latest = V5;
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

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Single source of truth for Engine API REST request-size limits.
/// </summary>
/// <remarks>
/// Per <c>execution-apis#793</c>: <c>MAX_BODIES_REQUEST = 2**5 = 32</c>,
/// <c>MAX_BLOBS_REQUEST = 2**7 = 128</c>. These values are advertised via
/// <c>GET /engine/v1/capabilities</c> and enforced by the corresponding handlers.
/// </remarks>
public static class SszRestLimits
{
    public const int MaxBodiesRequest = 32;
    public const int MaxBlobsRequest = 128;

    // ExecutionWitness bounds (execution-apis#885); the per-item ones come from the stateless spec.
    public const int MaxWitnessItems = 1 << 20;
    public const int MaxBytesPerWitnessNode = 1 << 10;
    public const int MaxBytesPerCode = 1 << 16;
    public const int MaxBytesPerHeader = 1 << 10;
    public const int MaxWitnessHeaders = 1 << 8;
}

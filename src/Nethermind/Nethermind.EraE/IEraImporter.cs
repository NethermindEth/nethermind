// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.EraE;

public interface IEraImporter: Era1.IEraImporter
{
    Task Import(
        string src,
        long from,
        long to,
        string? accumulatorFile = null,
        string? historicalRootsFile = null,
        CancellationToken cancellation = default);
}

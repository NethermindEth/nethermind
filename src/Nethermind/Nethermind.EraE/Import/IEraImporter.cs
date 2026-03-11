// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.EraE.Import;

public interface IEraImporter
{
    Task Import(string src, long from, long to, string? accumulatorFile = null, CancellationToken cancellation = default);
}

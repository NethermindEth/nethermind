// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.EraE.Import;

public interface IEraImporter
{
    Task Import(string src, ulong from, ulong to, string? accumulatorFile = null, CancellationToken cancellation = default);
}

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.EraE.Export;

public interface IEraExporter
{
    Task Export(string destinationPath, long from, long to, CancellationToken cancellation = default);
}

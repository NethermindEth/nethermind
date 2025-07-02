// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Era1;

public interface IEraExporter
{
    Task Export(string destinationPath, long from, long to, CancellationToken cancellation = default);
}

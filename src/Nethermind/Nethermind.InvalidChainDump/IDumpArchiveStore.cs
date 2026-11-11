// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.InvalidChainDump;

internal interface IDumpArchiveStore
{
    /// <summary>Stores the archive at <paramref name="archivePath"/> under its file name.</summary>
    /// <returns>The key the archive is stored under.</returns>
    Task<string> UploadAsync(string archivePath, CancellationToken cancellationToken);
}

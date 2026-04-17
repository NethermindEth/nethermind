// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.EraE.Store;

public readonly record struct RemoteEraEntry(string Filename, byte[] Sha256Hash);

public interface IRemoteEraClient
{
    /// <summary>Fetches and parses the remote checksum manifest into a map of epoch → entry.</summary>
    Task<IReadOnlyDictionary<int, RemoteEraEntry>> FetchManifestAsync(CancellationToken cancellation = default);

    /// <summary>
    /// Downloads a single erae file to <paramref name="destinationPath"/>.
    /// Uses an atomic write: streams to a .tmp file then renames on success.
    /// Deletes the .tmp file on failure.
    /// </summary>
    Task DownloadFileAsync(string filename, string destinationPath, CancellationToken cancellation = default);
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using CkzgLib;

namespace Nethermind.BeaconChain.DataAvailability;

/// <summary>
/// Lazily loads a c-kzg-4844 trusted setup for this assembly's own KZG cell verification and
/// reconstruction calls.
/// </summary>
/// <remarks>
/// <see cref="Nethermind.Crypto.KzgPolynomialCommitments"/> already loads and holds one, but its
/// native setup handle is <c>internal</c> to Nethermind.Crypto and there is no public accessor -
/// deliberately not added here, since this plugin's PeerDAS work must not modify Nethermind.Crypto.
/// This therefore loads a second, independent copy of the same trusted setup file (which
/// <c>Nethermind.Crypto</c>'s own <c>Content</c> item already copies next to every dependent
/// assembly's output). Consolidating onto one shared native handle is future cleanup, not a
/// correctness concern: both handles are loaded from the same file.
/// </remarks>
internal static class DasKzgSetup
{
    private const string TrustedSetupFileName = "kzg_trusted_setup.txt";

    /// <summary>Precompute level for the cell-proof tables, matching KzgPolynomialCommitments's own load call.</summary>
    private const int PrecomputeLevel = 8;

    private static nint _handle = nint.Zero;
    private static readonly Lock s_lock = new();

    public static nint Handle
    {
        get
        {
            nint handle = Volatile.Read(ref _handle);
            if (handle != nint.Zero)
            {
                return handle;
            }

            lock (s_lock)
            {
                if (_handle == nint.Zero)
                {
                    string path = Path.Combine(AppContext.BaseDirectory, TrustedSetupFileName);
                    nint loaded = Ckzg.LoadTrustedSetup(path, PrecomputeLevel);
                    if (loaded == nint.Zero)
                    {
                        throw new InvalidOperationException($"Failed to load KZG trusted setup from '{path}'.");
                    }
                    Volatile.Write(ref _handle, loaded);
                }
                return _handle;
            }
        }
    }
}

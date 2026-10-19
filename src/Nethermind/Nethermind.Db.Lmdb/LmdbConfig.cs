// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db.Lmdb;

public class LmdbConfig : ILmdbConfig
{
    public bool Enabled { get; set; }
    public long InitialMapSizeBytes { get; set; } = 68719476736; // 64 GiB
    public int PageSizeBytes { get; set; }
    public int PageSizeBytesAccount { get; set; }
    public int PageSizeBytesStorage { get; set; }
    public int MaxReaders { get; set; } = 8192;
    public int MaxOperationsPerWave { get; set; } = 4096;
    public int MaxOperationsPerWaveAccount { get; set; }
    public int MaxOperationsPerWaveStorage { get; set; }
    public bool EnableAccountPartitioning { get; set; }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db.Lmdb;

public class LmdbConfig : ILmdbConfig
{
    public bool Enabled { get; set; }
    public long InitialMapSizeBytes { get; set; } = 68719476736; // 64 GiB
    public int MaxReaders { get; set; } = 8192;
}

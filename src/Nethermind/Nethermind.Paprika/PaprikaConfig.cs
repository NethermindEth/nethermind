// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Paprika;

public class PaprikaConfig: IPaprikaConfig
{
    public bool Enabled { get; set; } = false;
    public long SizeGb { get; set; } = 512;
    public byte HistoryDepth { get; set; } = 128;
    public bool FlushToDisk { get; set; } = false;
    public int FinalizationQueueLimit { get; set; } = 2;
    public int AutomaticallyFinalizeAfter { get; set; } = 4;
    public bool ImportFromTrieStore { get; set; }
}

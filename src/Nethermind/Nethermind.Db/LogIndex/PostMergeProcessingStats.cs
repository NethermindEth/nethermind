// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db.LogIndex;

public class PostMergeProcessingStats
{
    public ExecTimeStats DBReading { get; set; } = new();
    public ExecTimeStats CompressingValue { get; set; } = new();
    public ExecTimeStats DBSaving { get; set; } = new();
    public int QueueLength { get; set; }

    public long CompressedAddressKeys;
    public long CompressedTopicKeys;

    public ExecTimeStats Total { get; } = new();

    public void Combine(PostMergeProcessingStats other)
    {
        DBReading.Combine(other.DBReading);
        CompressingValue.Combine(other.CompressingValue);
        DBSaving.Combine(other.DBSaving);

        CompressedAddressKeys += other.CompressedAddressKeys;
        CompressedTopicKeys += other.CompressedTopicKeys;

        Total.Combine(other.Total);
    }
}

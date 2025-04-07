﻿// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db;

public class PostMergeProcessingStats
{
    public ExecTimeStats GettingValue { get; set; } = new();
    public ExecTimeStats CompressingValue { get; set; } = new();
    public ExecTimeStats PuttingValues { get; set; } = new();
    public ExecTimeStats CommitingBatch { get; set; } = new();

    public long CompressedAddressKeys;
    public long CompressedTopicKeys;

    public ExecTimeStats Execution { get; } = new();

    public void Combine(PostMergeProcessingStats other)
    {
        GettingValue.Combine(other.GettingValue);
        CompressingValue.Combine(other.CompressingValue);
        PuttingValues.Combine(other.PuttingValues);
        CommitingBatch.Combine(other.CommitingBatch);

        CompressedAddressKeys += other.CompressedAddressKeys;
        CompressedTopicKeys += other.CompressedTopicKeys;

        Execution.Combine(other.Execution);
    }
}

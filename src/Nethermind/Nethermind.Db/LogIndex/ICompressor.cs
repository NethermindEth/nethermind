// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Db.LogIndex;

partial class LogIndexStorage<TPosition>
{
    /// <summary>
    /// Does background compression for keys with the number of blocks above the threshold.
    /// </summary>
    public interface ICompressor : IDisposable
    {
        int MinLengthToCompress { get; }
        PostMergeProcessingStats GetAndResetStats();

        bool TryEnqueue(int? topicIndex, ReadOnlySpan<byte> dbKey, ReadOnlySpan<byte> dbValue);
        Task EnqueueAsync(int? topicIndex, byte[] dbKey);
        Task WaitUntilEmptyAsync(TimeSpan waitTime = default, CancellationToken cancellationToken = default);

        void Start();
        Task StopAsync();
    }

    public sealed class NoOpCompressor : ICompressor
    {
        public int MinLengthToCompress => 256;
        public PostMergeProcessingStats Stats { get; } = new();
        public PostMergeProcessingStats GetAndResetStats() => Stats;
        public bool TryEnqueue(int? topicIndex, ReadOnlySpan<byte> dbKey, ReadOnlySpan<byte> dbValue) => false;
        public Task EnqueueAsync(int? topicIndex, byte[] dbKey) => Task.CompletedTask;
        public Task WaitUntilEmptyAsync(TimeSpan waitTime, CancellationToken cancellationToken) => Task.CompletedTask;
        public void Start() { }
        public Task StopAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
}

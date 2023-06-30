// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie
{
    public class TrieVisitContext : IDisposable
    {
        private SemaphoreSlim? _semaphore;
        private readonly int _maxDegreeOfParallelism = 1;
        private int _visitedNodes;

        public int Level { get; internal set; }
        public bool IsStorage { get; internal set; }
        public int? BranchChildIndex { get; internal set; }
        public bool ExpectAccounts { get; init; }

        public bool KeepTrackOfAbsolutePath { get; init; }

        public ITrieNodeResolver StorageTrieNodeResolver { get; init; }

        private List<byte>? _absolutePathNibbles;

        public List<byte> AbsolutePathNibbles => _absolutePathNibbles ??= new List<byte>();
        public int VisitedNodes => _visitedNodes;

        public int MaxDegreeOfParallelism
        {
            get => _maxDegreeOfParallelism;
            internal init => _maxDegreeOfParallelism = value == 0 ? Environment.ProcessorCount : value;
        }

        public AbsolutePathStruct AbsolutePathNext(byte[] path)
        {
            return new AbsolutePathStruct(!KeepTrackOfAbsolutePath ? null : AbsolutePathNibbles, path);
        }

        public AbsolutePathStruct AbsolutePathNext(byte path)
        {
            return new AbsolutePathStruct(!KeepTrackOfAbsolutePath ? null : AbsolutePathNibbles, path);
        }


        public SemaphoreSlim Semaphore
        {
            get
            {
                if (_semaphore is null)
                {
                    if (MaxDegreeOfParallelism == 1) throw new InvalidOperationException("Can not create semaphore for single threaded trie visitor.");
                    _semaphore = new SemaphoreSlim(MaxDegreeOfParallelism, MaxDegreeOfParallelism);
                }

                return _semaphore;
            }
        }

        public TrieVisitContext Clone() => (TrieVisitContext)MemberwiseClone();

        public void Dispose()
        {
            _semaphore?.Dispose();
        }

        public void AddVisited()
        {
            int visitedNodes = Interlocked.Increment(ref _visitedNodes);

            // TODO: Fine tune interval? Use TrieNode.GetMemorySize(false) to calculate memory usage?
            if (visitedNodes % 1_000_000 == 0)
            {
                GC.Collect();
            }

        }
    }

    public readonly ref struct AbsolutePathStruct
    {
        public AbsolutePathStruct(List<byte>? absolutePath, byte[]? path)
        {
            _absolutePath = absolutePath;
            _pathLength = path!.Length;
            _absolutePath?.AddRange(path!);
        }

        public AbsolutePathStruct(List<byte>? absolutePath, byte path)
        {
            _absolutePath = absolutePath;
            _pathLength = 1;
            _absolutePath?.Add(path);
        }

        private readonly List<byte>? _absolutePath;
        private readonly int _pathLength;

        public void Dispose()
        {
            if (_pathLength > 0)
                _absolutePath?.RemoveRange(_absolutePath.Count - _pathLength, _pathLength);
        }
    }
}

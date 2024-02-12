// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie
{
    public class TrieVisitContext : IDisposable
    {
        private SemaphoreSlim? _semaphore;
        private readonly int _maxDegreeOfParallelism = 1;
        private int _visitedNodes;

        public int Level { get; internal set; }
        public bool IsStorage => Storage != null;
        public int? BranchChildIndex { get; internal set; }
        public bool ExpectAccounts { get; init; }
        public int VisitedNodes => _visitedNodes;

        public int MaxDegreeOfParallelism
        {
            get => _maxDegreeOfParallelism;
            internal init => _maxDegreeOfParallelism = VisitingOptions.AdjustMaxDegreeOfParallelism(value);
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

        public Hash256? Storage { get; set; }

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

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SmallTrieVisitContext
    {
        public SmallTrieVisitContext(TrieVisitContext trieVisitContext)
        {
            Level = (byte)trieVisitContext.Level;
            BranchChildIndex = (byte?)trieVisitContext.BranchChildIndex;
            ExpectAccounts = trieVisitContext.ExpectAccounts;
            Storage = trieVisitContext.Storage;
        }

        public bool IsStorage => Storage != null;
        public Hash256? Storage { get; internal set; }
        public byte Level { get; internal set; }
        private byte _branchChildIndex = 255;
        private byte _flags = 0;

        private const byte ExpectAccountsFlag = 1;

        public bool ExpectAccounts
        {
            readonly get => (_flags & ExpectAccountsFlag) == ExpectAccountsFlag;
            internal set
            {
                if (value)
                {
                    _flags = (byte)(_flags | ExpectAccountsFlag);
                }
                else
                {
                    _flags = (byte)(_flags & ~ExpectAccountsFlag);
                }
            }
        }

        public byte? BranchChildIndex
        {
            readonly get => _branchChildIndex == 255 ? null : _branchChildIndex;
            internal set
            {
                if (value is null)
                {
                    _branchChildIndex = 255;
                }
                else
                {
                    _branchChildIndex = (byte)value;
                }
            }
        }

        public readonly TrieVisitContext ToVisitContext()
        {
            return new TrieVisitContext()
            {
                Level = Level,
                Storage = Storage,
                BranchChildIndex = BranchChildIndex,
                ExpectAccounts = ExpectAccounts
            };
        }
    }
}

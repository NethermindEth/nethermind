// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;

namespace Nethermind.Trie
{
    public struct OldTrieVisitContext : INodeContext<OldTrieVisitContext>
    {
        public int Level;
        public bool IsStorage;
        public int? BranchChildIndex;

        public OldTrieVisitContext Add(ReadOnlySpan<byte> nibblePath)
        {
            return this;
        }

        public OldTrieVisitContext Add(byte nibble)
        {
            return new OldTrieVisitContext
            {
                Level = Level,
                IsStorage = IsStorage,
                BranchChildIndex = nibble,
            };
        }

        public OldTrieVisitContext AddStorage(in ValueHash256 storage)
        {
            return new OldTrieVisitContext
            {
                Level = Level,
                IsStorage = true,
                BranchChildIndex = BranchChildIndex,
            };
        }
    }

    public class TrieVisitContext : IDisposable
    {
        private readonly int _maxDegreeOfParallelism = 1;
        private int _visitedNodes;

        private ConcurrencyController? _threadLimiter = null;
        public ConcurrencyController ConcurrencyController => _threadLimiter ??= new ConcurrencyController(MaxDegreeOfParallelism);

        public int Level { get; internal set; }
        public bool IsStorage { get; set; }
        public int MaxDegreeOfParallelism
        {
            get => _maxDegreeOfParallelism;
            internal init
            {
                _maxDegreeOfParallelism = VisitingOptions.AdjustMaxDegreeOfParallelism(value);
                _threadLimiter = null;
            }
        }

        public TrieVisitContext Clone() => (TrieVisitContext)MemberwiseClone();

        public void Dispose()
        {
        }

        public void AddVisited()
        {
            int visitedNodes = Interlocked.Increment(ref _visitedNodes);

            // TODO: Fine tune interval? Use TrieNode.GetMemorySize(false) to calculate memory usage?
            if (visitedNodes % 10_000_000 == 0)
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
            IsStorage = trieVisitContext.IsStorage;
        }

        public byte Level { get; internal set; }
        private byte _branchChildIndex = 255;
        private byte _flags = 0;

        private const byte StorageFlag = 1;
        private const byte ExpectAccountsFlag = 2;

        public bool IsStorage
        {
            readonly get => (_flags & StorageFlag) == StorageFlag;
            internal set
            {
                if (value)
                {
                    _flags = (byte)(_flags | StorageFlag);
                }
                else
                {
                    _flags = (byte)(_flags & ~StorageFlag);
                }
            }
        }

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

        public readonly TrieVisitContext ToVisitContext()
        {
            return new TrieVisitContext()
            {
                Level = Level,
                IsStorage = IsStorage,
            };
        }
    }
}

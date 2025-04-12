// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie
{
    public readonly struct OldStyleTrieVisitContext(int level, bool isStorage, int? branchChildIndex) : INodeContext<OldStyleTrieVisitContext>
    {
        public readonly int Level = level;
        public readonly bool IsStorage = isStorage;
        public readonly int? BranchChildIndex = branchChildIndex;

        public OldStyleTrieVisitContext Add(ReadOnlySpan<byte> nibblePath)
        {
            return new(Level + 1, IsStorage, null);
        }

        public OldStyleTrieVisitContext Add(byte nibble)
        {
            return new(Level + 1, IsStorage, nibble);
        }

        public OldStyleTrieVisitContext AddStorage(in ValueHash256 storage)
        {
            return new(Level + 1, true, null);
        }
    }

    public class TrieVisitContext : IDisposable
    {
        private readonly int _maxDegreeOfParallelism = 1;

        public int MaxDegreeOfParallelism
        {
            get => _maxDegreeOfParallelism;
            internal init
            {
                _maxDegreeOfParallelism = VisitingOptions.AdjustMaxDegreeOfParallelism(value);
            }
        }

        public bool IsStorage { get; set; }

        public void Dispose()
        {
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SmallTrieVisitContext
    {
        public SmallTrieVisitContext(TrieVisitContext trieVisitContext)
        {
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
    }
}

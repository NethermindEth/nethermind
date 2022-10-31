//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.Trie
{
    public class TrieVisitContext : IDisposable
    {
        private SemaphoreSlim? _semaphore;
        private readonly int _maxDegreeOfParallelism = 1;

        public int Level { get; internal set; }
        public bool IsStorage { get; internal set; }
        public int? BranchChildIndex { get; internal set; }
        public bool ExpectAccounts { get; init; }
        public bool KeepTrackOfAbsolutePath { get; init; }

        private List<byte>? _absolutePathNibbles;

        public List<byte> AbsolutePathNibbles => _absolutePathNibbles ??= new List<byte>();

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
                    if (MaxDegreeOfParallelism == 1)
                        throw new InvalidOperationException(
                            "Can not create semaphore for single threaded trie visitor.");
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

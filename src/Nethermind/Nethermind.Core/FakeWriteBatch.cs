// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    public class FakeWriteBatch(IWriteOnlyKeyValueStore storePretendingToSupportBatches, Action? onDispose) : IWriteBatch
    {
        private readonly IWriteOnlyKeyValueStore _storePretendingToSupportBatches = storePretendingToSupportBatches;

        private readonly Action? _onDispose = onDispose;

        public FakeWriteBatch(IWriteOnlyKeyValueStore storePretendingToSupportBatches)
            : this(storePretendingToSupportBatches, null)
        {
        }

        public void Dispose()
        {
            _onDispose?.Invoke();
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            _storePretendingToSupportBatches.Set(key, value, flags);
        }

        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
        {
            throw new NotSupportedException("Merging is not supported by this implementation.");
        }

        public void Clear() { }
    }
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    public class FakeWriteBatch : IWriteBatch
    {
        private readonly IWriteOnlyKeyValueStore _storePretendingToSupportBatches;

        private readonly Action? _onDispose;

        public FakeWriteBatch(IWriteOnlyKeyValueStore storePretendingToSupportBatches)
            : this(storePretendingToSupportBatches, null)
        {
        }

        public FakeWriteBatch(IWriteOnlyKeyValueStore storePretendingToSupportBatches, Action? onDispose)
        {
            _storePretendingToSupportBatches = storePretendingToSupportBatches;
            _onDispose = onDispose;
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

        public void Clear()
        {
            throw new NotSupportedException("Cancelling is not supported by this implementation.");
        }
    }
}

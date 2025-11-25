// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

public class RefCountingPersistenceReader(IPersistence.IPersistenceReader innerReader): RefCountingDisposable, IPersistence.IPersistenceReader
{
    public bool TryGetAccount(Address address, out Account? acc)
    {
        return innerReader.TryGetAccount(address, out acc);
    }

    public bool TryGetSlot(Address address, in UInt256 index, out byte[] value)
    {
        return innerReader.TryGetSlot(address, in index, out value);
    }

    public StateId CurrentState => innerReader.CurrentState;

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags)
    {
        return innerReader.TryLoadRlp(address, in path, hash, flags);
    }

    protected override void CleanUp()
    {
        innerReader.Dispose();
    }
}

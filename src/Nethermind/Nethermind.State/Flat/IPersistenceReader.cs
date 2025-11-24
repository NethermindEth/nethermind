// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

public interface IPersistenceReader: IDisposable
{
    bool TryGetAccount(Address address, out Account acc);
    bool TryGetSlot(Address address, in UInt256 index, out byte[] value);
    StateId CurrentState { get; }
    byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags);
}

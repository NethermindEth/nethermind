// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Ssz;

public interface IKeyValueStore<in TKey, TValue>
{
    byte[]? this[TKey key] { get; set; }
}

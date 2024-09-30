// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.State;

/// <summary>
/// Provides state access for the given state root.
/// Can be obtained via <see cref="IStateReader.ForStateRoot"/>.
/// </summary>
public interface IScopedStateReader : IDisposable
{
    bool TryGetAccount(Address address, out AccountStruct account);

    ReadOnlySpan<byte> GetStorage(Address address, in UInt256 index);

    Hash256 StateRoot { get; }
}

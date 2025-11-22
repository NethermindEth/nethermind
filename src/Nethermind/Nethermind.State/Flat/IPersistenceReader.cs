// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.State.Flat;

public interface IPersistenceReader: IDisposable
{
    bool TryGetAccount(Address address, out Account acc);
    bool TryGetSlot(Address address, in UInt256 index, out byte[] value);
    StateId CurrentState { get; }
}

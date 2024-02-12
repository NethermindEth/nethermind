// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.State.Transition;

public interface IMerkleStateIterator
{
    IEnumerable<(Address, Account)> GetAccountIterator(Hash256 startAddressKey);
    IEnumerable<(StorageCell, byte[])> GetStorageSlotsIterator(Address addressKey, Hash256 startIndexHash);
}

// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.TxPool;

public interface ITxStorage
{
    Transaction? Get(Keccak hash);
    Transaction?[] GetAll();
    void Add(Transaction transaction);
    void Remove(Keccak hash);
}

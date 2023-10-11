// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.TxPool;

public interface ITxStorage
{
    bool TryGet(in ValueKeccak hash, Address sender, in UInt256 timestamp, [NotNullWhen(true)] out Transaction? transaction);
    IEnumerable<LightTransaction> GetAll();
    void Add(Transaction transaction);
    void Delete(in ValueKeccak hash, in UInt256 timestamp);
}

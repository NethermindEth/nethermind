// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Xdc.Contracts;

public interface ITrc21StateReader
{
    IReadOnlyDictionary<Address, UInt256> GetFeeCapacities(XdcBlockHeader? baseBlock);
    bool ValidateTransaction(XdcBlockHeader? baseBlock, Address from, Address token, ReadOnlySpan<byte> data);
}

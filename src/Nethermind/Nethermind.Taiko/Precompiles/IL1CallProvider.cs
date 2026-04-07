// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Taiko.Precompiles;

public interface IL1CallProvider
{
    byte[]? ExecuteStaticCall(Address contractAddress, UInt256 blockNumber, byte[] calldata);
}

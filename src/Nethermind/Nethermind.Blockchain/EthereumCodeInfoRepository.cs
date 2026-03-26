// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.State;
using Nethermind.Core;

namespace Nethermind.Blockchain;

// Mainly used by tests
public class EthereumCodeInfoRepository(IWorldState worldState, IBlockAccessListBuilder balBuilder) : Evm.CacheCodeInfoRepository(worldState, new EthereumPrecompileProvider(), balBuilder)
{
}

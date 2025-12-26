// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm;
using Nethermind.Evm.State;

namespace Nethermind.Blockchain;

// Mainly used by tests
public class EthereumCodeInfoRepository(IWorldState worldState) : CodeInfoRepository(worldState, new EthereumPrecompileProvider())
{
}

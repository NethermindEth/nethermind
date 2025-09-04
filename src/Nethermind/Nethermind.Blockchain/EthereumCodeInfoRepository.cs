// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm;

namespace Nethermind.Blockchain;

public class EthereumCodeInfoRepository() : CodeInfoRepository(new EthereumPrecompileFactory())
{
}

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Blockchain.Precompiles;
using Nethermind.State;

namespace Nethermind.Blockchain;

public class EthereumCodeInfoRepository(
    ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, (byte[], bool)>? precompileCache = null)
    : CachedCodeInfoRepository(EthereumPrecompiles.Default, precompileCache);

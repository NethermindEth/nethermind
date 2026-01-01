// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Headers;

public interface IHeaderFinder
{
    BlockHeader? Get(Hash256 blockHash, ulong? blockNumber = null);
}

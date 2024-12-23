// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Synchronization.FastSync;

public interface IBlockingVerifyTrie
{
    bool TryStartVerifyTrie(Hash256 rootNode);
}

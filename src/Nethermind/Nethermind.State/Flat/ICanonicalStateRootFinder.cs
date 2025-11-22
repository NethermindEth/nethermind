// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat;

public interface ICanonicalStateRootFinder
{
    public Hash256? GetCanonicalStateRootAtBlock(long blockNumber);
}

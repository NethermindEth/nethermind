// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.StateStore;

public interface IStateStoreExtension : IReadOnlyStateStore
{
}

public interface IVerkleStateStoreExtension : IVerkleReadOnlyStateStore
{
}


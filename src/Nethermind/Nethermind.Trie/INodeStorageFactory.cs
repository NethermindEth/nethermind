// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Trie;

public interface INodeStorageFactory
{
    INodeStorage WrapKeyValueStore(IKeyValueStore keyValueStore);
}

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Synchronization.SnapSync;

public interface ISnapTrieFactory
{
    ISnapTree CreateStateTree();
    ISnapTree CreateStorageTree(in ValueHash256 accountPath);
}

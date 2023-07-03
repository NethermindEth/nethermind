// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie;

public class MissingRootHashException : TrieException
{
    public MissingRootHashException(ValueKeccak rootHash, Exception baseException) : base($"Failed to load root hash {rootHash}.", baseException)
    {
    }
}

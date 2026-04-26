// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie;

public class TrieNodeException(string message, TreePath path, Hash256 keccak, Exception? inner = null)
    : TrieException(message, inner)
{
    public ValueHash256 NodeHash { get; private set; } = keccak;
    public TreePath Path { get; private set; } = path;
    public string? EnhancedMessage { get; set; }
    public override string Message => EnhancedMessage is null ? base.Message : EnhancedMessage + Environment.NewLine + base.Message;
}

// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie;

public class TrieNodeException : TrieException
{
    public ValueKeccak NodeHash { get; private set; }
    public string? EnhancedMessage { get; set; }
    public override string Message => EnhancedMessage is null ? base.Message : EnhancedMessage + Environment.NewLine + base.Message;

    public TrieNodeException(string message, Keccak keccak, Exception? inner = null) : base(message, inner)
    {
        NodeHash = keccak;
    }
}

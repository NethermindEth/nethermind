// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Trie;

public sealed partial class TrieNode
{
    internal bool TryResolveNodeFromRlp(byte[]? fullRlp)
    {
        if (NodeType != NodeType.Unknown) return true;
        try
        {
            CappedArray<byte> rlp = ReadRlp();
            if (rlp.IsNull)
            {
                if (fullRlp is null || Keccak is null) return false;
                if (NodeType != NodeType.Unknown) return true;
                WriteRlp(rlp = new CappedArray<byte>(fullRlp));
                IsPersisted = true;
            }
            return TryDecodeRlp(in rlp, null);
        }
        catch (RlpException)
        {
            return false;
        }
    }
}

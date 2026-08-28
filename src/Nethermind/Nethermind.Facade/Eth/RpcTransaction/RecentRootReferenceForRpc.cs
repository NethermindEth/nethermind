// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Facade.Eth.RpcTransaction;

/// <summary>
/// JSON-RPC view of an EIP-8272 recent-root reference <c>[source_id, slot, root]</c>.
/// </summary>
/// <remarks>An absent list and an empty one are different transactions; the mapping keeps them apart.</remarks>
public class RecentRootReferenceForRpc
{
    public Hash256 SourceId { get; set; } = Keccak.Zero;
    public ulong Slot { get; set; }
    public Hash256 Root { get; set; } = Keccak.Zero;

    [JsonConstructor]
    public RecentRootReferenceForRpc() { }

    public RecentRootReferenceForRpc(in RecentRootReference reference)
    {
        SourceId = new Hash256(reference.SourceId);
        Slot = reference.Slot;
        Root = new Hash256(reference.Root);
    }

    public RecentRootReference ToReference() => new(SourceId.ValueHash256, Slot, Root.ValueHash256);

    public static RecentRootReferenceForRpc[]? FromReferences(RecentRootReference[]? references)
    {
        if (references is null) return null;

        RecentRootReferenceForRpc[] result = new RecentRootReferenceForRpc[references.Length];
        for (int i = 0; i < references.Length; i++)
        {
            result[i] = new RecentRootReferenceForRpc(references[i]);
        }

        return result;
    }

    public static RecentRootReference[]? ToReferences(RecentRootReferenceForRpc[]? references)
    {
        if (references is null) return null;

        RecentRootReference[] result = new RecentRootReference[references.Length];
        for (int i = 0; i < references.Length; i++)
        {
            result[i] = references[i].ToReference();
        }

        return result;
    }
}

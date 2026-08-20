// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
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
    // System.Text.Json assigns a JSON null straight past a reference-type converter, so it overrides these
    // initializers rather than being rejected by them; TryToReferences is what turns that into an error.
    public Hash256? SourceId { get; set; } = Keccak.Zero;
    public ulong Slot { get; set; }
    public Hash256? Root { get; set; } = Keccak.Zero;

    [JsonConstructor]
    public RecentRootReferenceForRpc() { }

    public RecentRootReferenceForRpc(in RecentRootReference reference)
    {
        SourceId = new Hash256(reference.SourceId);
        Slot = reference.Slot;
        Root = new Hash256(reference.Root);
    }

    /// <remarks>Reach this through <see cref="TryToReferences"/>, which is what rules out the null hashes.</remarks>
    public RecentRootReference ToReference() => new(SourceId!.ValueHash256, Slot, Root!.ValueHash256);

    public static RecentRootReferenceForRpc[]? FromReferences(RecentRootReference[]? references) =>
        references?.Select(static r => new RecentRootReferenceForRpc(r)).ToArray();

    /// <summary>Maps the deserialized <c>recentRootReferences</c> list onto the transaction's references.</summary>
    /// <param name="references">The deserialized list, or <c>null</c> when the request omitted it.</param>
    /// <param name="converted">The mapped list, or <c>null</c> when <paramref name="references"/> is absent.</param>
    /// <returns><c>false</c> if an element, or either hash of one, was JSON <c>null</c>.</returns>
    public static bool TryToReferences(RecentRootReferenceForRpc[]? references, out RecentRootReference[]? converted)
    {
        converted = null;
        foreach (RecentRootReferenceForRpc reference in references ?? [])
        {
            if (reference is null or { SourceId: null } or { Root: null }) return false;
        }

        return RpcListConverter.TryConvert(references, static r => r.ToReference(), out converted);
    }
}

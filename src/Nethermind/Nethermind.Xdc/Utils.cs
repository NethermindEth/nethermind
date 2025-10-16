// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.IdentityModel.Tokens;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc;
using Nethermind.Xdc.Errors;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ZstdSharp.Unsafe;

namespace Nethermind.Xdc;
internal static class Utils
{
    public static Address[] GetMasternodesFromGenesisHeader(IBlockTree chain, XdcBlockHeader genesisHeader)
    {
        if (genesisHeader == null)
        {
            return [];
        }

        int size = (genesisHeader.ExtraData.Length - 65 - 32) / Address.Size;
        Address[] masterNodes = new Address[size];

        int startOffset = 32;
        for (int i = 0; i < masterNodes.Length; i++)
        {
            masterNodes[i] = new Address(genesisHeader.ExtraData.Slice(startOffset + i * Address.Size, Address.Size));
        }

        return masterNodes;
    }

    public static T[] RemoveItemFromArray<T>(T[] candidates, T[] penalties, int withMaxCap = int.MaxValue)
    {
        if (penalties == null || penalties.Length == 0)
            return candidates; // nothing to remove

        var penaltySet = new HashSet<T>(penalties); // O(penalties.Length)

        // allocate result with upper bound = candidates.Length
        var result = new T[candidates.Length];
        int idx = 0;

        for (int i = 0; i < candidates.Length; i++)
        {
            if (!penaltySet.Contains(candidates[i]))
            {
                result[idx++] = candidates[i];
            }
        }

        if (idx == result.Length)
            return result; // no removals happened

        // trim excess
        Array.Resize(ref result, Math.Min(withMaxCap, idx));
        return result;
    }

    internal static bool TryGetExtraFields(XdcBlockHeader header, long switchBlock, out ExtraFieldsV2? consensusData, out Address[] masterNodes)
    {
        if (header.Number == switchBlock)
        {
            masterNodes = GetMasterNodesFromHeaderExtra(header);
            consensusData = default;
            return true;
        }

        masterNodes = header.ValidatorsAddress.Value.ToArray();
        consensusData = header.ExtraConsensusData;

        if (consensusData is null)
        {
            return false;
        }

        return true;
    }

    private static Address[] GetMasterNodesFromHeaderExtra(XdcBlockHeader header)
    {
        if (header.Validators == null)
            throw new InvalidOperationException("Header has no validators.");
        Address[] masterNodes = new Address[(header.ExtraData.Length - XdcConstants.ExtraVanity - XdcConstants.ExtraSeal) / Address.Size];
        for (int i = 0; i < masterNodes.Length; i++)
        {
            masterNodes[i] = new Address(header.ExtraData.AsSpan(XdcConstants.ExtraVanity + i * Address.Size, Address.Size));
        }
        return masterNodes;
    }

    internal static (HashSet<Signature> Unique, List<Signature> Duplicates) FilterSignatures(Signature[] signatures)
    {
        var seen = new HashSet<Signature>();
        var duplicates = new List<Signature>();

        foreach (var signature in signatures)
        {
            if (!seen.Add(signature))
            {
                duplicates.Add(signature);
            }
        }

        return (seen, duplicates);
    }

}

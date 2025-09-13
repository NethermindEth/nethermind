// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Xdc;
using Nethermind.Xdc.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
internal static class Utils
{
    public static ExpCountDown FromConfig(IXdcConfig hotStuffConfig)
        => new ExpCountDown(TimeSpan.FromSeconds(hotStuffConfig.CurrentConfig.TimeoutPeriod).Nanoseconds, hotStuffConfig.CurrentConfig.ExpTimeoutConfig.Base, hotStuffConfig.CurrentConfig.ExpTimeoutConfig.MaxExponent);
    public static Hash256 SignHash(XdcBlockHeader header)
    {
        throw new NotImplementedException();
    }

    public static Address[] GetMasternodesFromEpochSwitchHeader(IBlockTree chain, XdcBlockHeader epochSwitchHeader)
    {
        if (epochSwitchHeader == null)
        {
            return [];
        }

        Address[] masterNodes = new Address[epochSwitchHeader.Validators.Length / Address.Size];

        for (int i = 0; i < masterNodes.Length; i++)
        {
            masterNodes[i] = new Address(epochSwitchHeader.Validators.Slice(i * Address.Size, Address.Size));
        }

        return masterNodes;
    }

    public static Address[] GetMasternodesFromGenesisHeader(IBlockTree chain, XdcBlockHeader genesisHeader)
    {
        if (genesisHeader == null)
        {
            throw new Exception();
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

    internal static int Position(Address[] masternodes, Address signer)
    {
        return Array.BinarySearch(masternodes, signer);
    }

    internal static bool TryFindSigner(Address[] masternodes, Address signer, out int currentPosition)
    {
        return (currentPosition = Array.BinarySearch(masternodes, signer)) >= 0;
    }

    internal static Address[] ExtractAddressFromBytes(byte[] penalties)
    {
        if (penalties is null || penalties.Length < Address.Size)
        {
            return [];
        }

        var Addresses = new Address[penalties.Length / Address.Size];
        for (int i = 0; i < penalties.Length; i++)
        {
            int startIndex = i * Address.Size;
            var address = new Address(penalties[startIndex..(startIndex + Address.Size)]);
            Addresses[i] = address;
        }

        return Addresses;
    }
    internal static bool TryGetExtraFields(XdcBlockHeader header, long switchBlock, out QuorumCert quorumCert, out ulong round, out Address[] masterNodes)
    {
        if (header.Number == switchBlock)
        {
            masterNodes = header.GetMasterNodesFromHeaderExtra();
            quorumCert = default;
            round = 0;
            return true;
        }

        masterNodes = header.GetMasterNodesFromEpochSwitchHeader();

        if (!header.TryDecodeExtraFields(out ExtraFieldsV2 extraFields))
        {
            quorumCert = default;
            round = 0;
            return false;
        }

        quorumCert = extraFields.QuorumCert;
        round = extraFields.Round;

        return true;
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

    internal static bool IsAuthorisedAddress(ISnapshotManager snapshotManager, XdcBlockHeader header, Address address)
    {
        if (!snapshotManager.TryGetSnapshot(header, out Nethermind.Xdc.Types.Snapshot snapshot))
        {
            throw new Exception();
        }

        return Array.BinarySearch(snapshot.NextEpochCandidates, address) >= 0;
    }

    internal static bool TryCalcMasterNodes(IBlockTree chain, XdcBlockHeader parent, ulong currentRound, out Address[] masternodes, out object penalties)
    {
        throw new NotImplementedException();
    }

    internal static bool TryGetBlockByEpochNumber(IBlockTree tree, ulong tempTCEpoch, out Types.BlockInfo epochBlockInfo)
    {
        throw new NotImplementedException();
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.BeaconChain.Spec;

namespace Nethermind.BeaconChain.Types;

/// <summary>Encodes and decodes Electra/Fulu and Gloas light-client SSZ data.</summary>
/// <remarks>Wire-format checks do not verify signatures, Merkle proofs or consensus validity.</remarks>
public static class LightClientWireCodec
{
    /// <summary>Encodes a bootstrap using the fork format selected by its beacon header slot.</summary>
    /// <param name="value">The bootstrap to encode.</param>
    /// <param name="spec">The chain specification containing the Gloas activation epoch.</param>
    /// <returns>The Electra/Fulu or Gloas SSZ encoding.</returns>
    /// <exception cref="InvalidDataException">The bootstrap has no beacon header, or its header format disagrees with the fork at its slot.</exception>
    public static byte[] EncodeBootstrap(LightClientBootstrap value, BeaconChainSpec spec) =>
        IsGloas(value.Header, spec) ? GloasLightClientBootstrap.Encode(new GloasLightClientBootstrap
        {
            Header = ToGloas(value.Header!),
            CurrentSyncCommittee = value.CurrentSyncCommittee,
            CurrentSyncCommitteeBranch = value.CurrentSyncCommitteeBranch,
        }) : LightClientBootstrap.Encode(value);

    /// <summary>Decodes a canonical bootstrap using the fork format required by its beacon header slot.</summary>
    /// <param name="bytes">The complete Electra/Fulu or Gloas SSZ bootstrap.</param>
    /// <param name="spec">The chain specification containing the Gloas activation epoch.</param>
    /// <returns>The decoded bootstrap with its header format identified.</returns>
    /// <remarks>Checks fork consistency and byte-for-byte canonical re-encoding; cryptographic verification is separate.</remarks>
    /// <exception cref="InvalidDataException">Neither wire format is valid for the header slot, or both formats are accepted.</exception>
    public static LightClientBootstrap DecodeBootstrap(ReadOnlySpan<byte> bytes, BeaconChainSpec spec) => Decode(
        bytes, spec,
        static data => { LightClientBootstrap.Decode(data, out LightClientBootstrap value); return value; },
        static data =>
        {
            GloasLightClientBootstrap.Decode(data, out GloasLightClientBootstrap value);
            return new LightClientBootstrap
            {
                Header = FromGloas(value.Header),
                CurrentSyncCommittee = value.CurrentSyncCommittee,
                CurrentSyncCommitteeBranch = value.CurrentSyncCommitteeBranch
            };
        },
        static value => value.Header, EncodeBootstrap);

    /// <summary>Encodes an update using the fork format selected by its attested beacon header slot.</summary>
    /// <param name="value">The update to encode.</param>
    /// <param name="spec">The chain specification containing the Gloas activation epoch.</param>
    /// <returns>The Electra/Fulu or Gloas SSZ encoding.</returns>
    /// <exception cref="InvalidDataException">The update has no attested beacon header, or its header format disagrees with the fork at its slot.</exception>
    public static byte[] EncodeUpdate(LightClientUpdate value, BeaconChainSpec spec) =>
        IsGloas(value.AttestedHeader, spec) ? GloasLightClientUpdate.Encode(new GloasLightClientUpdate
        {
            AttestedHeader = ToGloas(value.AttestedHeader!),
            NextSyncCommittee = value.NextSyncCommittee,
            NextSyncCommitteeBranch = value.NextSyncCommitteeBranch,
            FinalizedHeader = ToGloas(value.FinalizedHeader!),
            FinalityBranch = value.FinalityBranch,
            SyncAggregate = value.SyncAggregate,
            SignatureSlot = value.SignatureSlot,
        }) : LightClientUpdate.Encode(value);

    /// <summary>Decodes a canonical update using the fork format required by its attested beacon header slot.</summary>
    /// <param name="bytes">The complete Electra/Fulu or Gloas SSZ update.</param>
    /// <param name="spec">The chain specification containing the Gloas activation epoch.</param>
    /// <returns>The decoded update with its header formats identified.</returns>
    /// <remarks>Checks fork consistency and byte-for-byte canonical re-encoding; cryptographic verification is separate.</remarks>
    /// <exception cref="InvalidDataException">Neither wire format is valid for the attested header slot, or both formats are accepted.</exception>
    public static LightClientUpdate DecodeUpdate(ReadOnlySpan<byte> bytes, BeaconChainSpec spec) => Decode(
        bytes, spec,
        static data => { LightClientUpdate.Decode(data, out LightClientUpdate value); return value; },
        static data =>
        {
            GloasLightClientUpdate.Decode(data, out GloasLightClientUpdate value);
            return new LightClientUpdate
            {
                AttestedHeader = FromGloas(value.AttestedHeader),
                NextSyncCommittee = value.NextSyncCommittee,
                NextSyncCommitteeBranch = value.NextSyncCommitteeBranch,
                FinalizedHeader = FromGloas(value.FinalizedHeader),
                FinalityBranch = value.FinalityBranch,
                SyncAggregate = value.SyncAggregate,
                SignatureSlot = value.SignatureSlot
            };
        },
        static value => value.AttestedHeader, EncodeUpdate);

    /// <summary>Encodes a finality update using the fork format selected by its attested beacon header slot.</summary>
    /// <param name="value">The finality update to encode.</param>
    /// <param name="spec">The chain specification containing the Gloas activation epoch.</param>
    /// <returns>The Electra/Fulu or Gloas SSZ encoding.</returns>
    /// <exception cref="InvalidDataException">The update has no attested beacon header, or its header format disagrees with the fork at its slot.</exception>
    public static byte[] EncodeFinality(LightClientFinalityUpdate value, BeaconChainSpec spec) =>
        IsGloas(value.AttestedHeader, spec) ? GloasLightClientFinalityUpdate.Encode(new GloasLightClientFinalityUpdate
        {
            AttestedHeader = ToGloas(value.AttestedHeader!),
            FinalizedHeader = ToGloas(value.FinalizedHeader!),
            FinalityBranch = value.FinalityBranch,
            SyncAggregate = value.SyncAggregate,
            SignatureSlot = value.SignatureSlot,
        }) : LightClientFinalityUpdate.Encode(value);

    /// <summary>Decodes a canonical finality update using the fork format required by its attested beacon header slot.</summary>
    /// <param name="bytes">The complete Electra/Fulu or Gloas SSZ finality update.</param>
    /// <param name="spec">The chain specification containing the Gloas activation epoch.</param>
    /// <returns>The decoded finality update with its header formats identified.</returns>
    /// <remarks>Checks fork consistency and byte-for-byte canonical re-encoding; cryptographic verification is separate.</remarks>
    /// <exception cref="InvalidDataException">Neither wire format is valid for the attested header slot, or both formats are accepted.</exception>
    public static LightClientFinalityUpdate DecodeFinality(ReadOnlySpan<byte> bytes, BeaconChainSpec spec) => Decode(
        bytes, spec,
        static data => { LightClientFinalityUpdate.Decode(data, out LightClientFinalityUpdate value); return value; },
        static data =>
        {
            GloasLightClientFinalityUpdate.Decode(data, out GloasLightClientFinalityUpdate value);
            return new LightClientFinalityUpdate
            {
                AttestedHeader = FromGloas(value.AttestedHeader),
                FinalizedHeader = FromGloas(value.FinalizedHeader),
                FinalityBranch = value.FinalityBranch,
                SyncAggregate = value.SyncAggregate,
                SignatureSlot = value.SignatureSlot
            };
        },
        static value => value.AttestedHeader, EncodeFinality);

    /// <summary>Encodes an optimistic update using the fork format selected by its attested beacon header slot.</summary>
    /// <param name="value">The optimistic update to encode.</param>
    /// <param name="spec">The chain specification containing the Gloas activation epoch.</param>
    /// <returns>The Electra/Fulu or Gloas SSZ encoding.</returns>
    /// <exception cref="InvalidDataException">The update has no attested beacon header, or its header format disagrees with the fork at its slot.</exception>
    public static byte[] EncodeOptimistic(LightClientOptimisticUpdate value, BeaconChainSpec spec) =>
        IsGloas(value.AttestedHeader, spec) ? GloasLightClientOptimisticUpdate.Encode(new GloasLightClientOptimisticUpdate
        {
            AttestedHeader = ToGloas(value.AttestedHeader!),
            SyncAggregate = value.SyncAggregate,
            SignatureSlot = value.SignatureSlot,
        }) : LightClientOptimisticUpdate.Encode(value);

    /// <summary>Decodes a canonical optimistic update using the fork format required by its attested beacon header slot.</summary>
    /// <param name="bytes">The complete Electra/Fulu or Gloas SSZ optimistic update.</param>
    /// <param name="spec">The chain specification containing the Gloas activation epoch.</param>
    /// <returns>The decoded optimistic update with its header format identified.</returns>
    /// <remarks>Checks fork consistency and byte-for-byte canonical re-encoding; cryptographic verification is separate.</remarks>
    /// <exception cref="InvalidDataException">Neither wire format is valid for the attested header slot, or both formats are accepted.</exception>
    public static LightClientOptimisticUpdate DecodeOptimistic(ReadOnlySpan<byte> bytes, BeaconChainSpec spec) => Decode(
        bytes, spec,
        static data => { LightClientOptimisticUpdate.Decode(data, out LightClientOptimisticUpdate value); return value; },
        static data =>
        {
            GloasLightClientOptimisticUpdate.Decode(data, out GloasLightClientOptimisticUpdate value);
            return new LightClientOptimisticUpdate
            {
                AttestedHeader = FromGloas(value.AttestedHeader),
                SyncAggregate = value.SyncAggregate,
                SignatureSlot = value.SignatureSlot
            };
        },
        static value => value.AttestedHeader, EncodeOptimistic);

    private static T Decode<T>(ReadOnlySpan<byte> bytes, BeaconChainSpec spec,
        Func<byte[], T> decodeLegacy, Func<byte[], T> decodeGloas,
        Func<T, LightClientHeader?> header, Func<T, BeaconChainSpec, byte[]> encode)
    {
        byte[] data = bytes.ToArray();
        bool validLegacy = TryDecode(data, spec, false, decodeLegacy, header, encode, out T? legacy);
        bool validGloas = TryDecode(data, spec, true, decodeGloas, header, encode, out T? gloas);
        if (validLegacy == validGloas)
            throw new InvalidDataException("Light-client SSZ response has an invalid or ambiguous fork format.");
        return validLegacy ? legacy! : gloas!;
    }

    private static bool TryDecode<T>(byte[] bytes, BeaconChainSpec spec, bool expectGloas,
        Func<byte[], T> decode, Func<T, LightClientHeader?> header, Func<T, BeaconChainSpec, byte[]> encode,
        out T? value)
    {
        value = default;
        try
        {
            value = decode(bytes);
            LightClientHeader? decodedHeader = header(value);
            if (decodedHeader?.Beacon is null || decodedHeader.IsGloas != expectGloas ||
                (spec.GetEpoch(decodedHeader.Beacon.Slot) >= spec.GloasForkEpoch) != expectGloas)
                return false;
            return bytes.AsSpan().SequenceEqual(encode(value, spec));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static bool IsGloas(LightClientHeader? header, BeaconChainSpec spec)
    {
        if (header?.Beacon is null) throw new InvalidDataException("Light-client response has no beacon header.");
        bool gloas = spec.GetEpoch(header.Beacon.Slot) >= spec.GloasForkEpoch;
        if (gloas != header.IsGloas) throw new InvalidDataException("Light-client header format does not match its fork.");
        return gloas;
    }

    private static GloasLightClientHeader ToGloas(LightClientHeader header) => new()
    {
        Beacon = header.Beacon,
        ExecutionBlockHash = header.ExecutionBlockHash,
        ExecutionBranch = header.ExecutionBranch,
    };

    private static LightClientHeader? FromGloas(GloasLightClientHeader? header) => header is null ? null : new()
    {
        Beacon = header.Beacon,
        ExecutionBlockHash = header.ExecutionBlockHash,
        ExecutionBranch = header.ExecutionBranch,
        IsGloas = true,
    };

}

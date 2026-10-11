// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Test.ForkChoice;
using Nethermind.BeaconChain.Test.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.BeaconChain.Test.Sync;

public enum InvalidSyncCommitteeKey
{
    Infinity,
    NotInG1,
    AggregateNotSumOfPubkeys,
}

internal static class SyncCommitteeKeyAnchors
{
    /// <summary>Use a later member so checking only the first committee key cannot pass.</summary>
    public const int Position = 7;

    public static byte[] EncodeState(bool gloas, bool nextCommittee, InvalidSyncCommitteeKey? key, out Hash256 blockRoot)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        if (gloas)
        {
            BeaconStateGloas state = chain.First.PostState.Clone();
            if (key is { } gloasKey)
            {
                if (nextCommittee) state.NextSyncCommittee = WithKey(state.NextSyncCommittee!, gloasKey);
                else state.CurrentSyncCommittee = WithKey(state.CurrentSyncCommittee!, gloasKey);
            }

            blockRoot = chain.First.Root;
            return BeaconStateGloas.Encode(state);
        }

        BeaconStateFulu fulu = chain.AnchorState.Clone();
        if (key is { } fuluKey)
        {
            if (nextCommittee) fulu.NextSyncCommittee = WithKey(fulu.NextSyncCommittee!, fuluKey);
            else fulu.CurrentSyncCommittee = WithKey(fulu.CurrentSyncCommittee!, fuluKey);
        }

        blockRoot = chain.AnchorRoot;
        return BeaconStateFulu.Encode(fulu);
    }

    public static string Field(bool nextCommittee) => nextCommittee ? "next_sync_committee" : "current_sync_committee";

    public static string Refusal(bool nextCommittee, InvalidSyncCommitteeKey key) => key == InvalidSyncCommitteeKey.AggregateNotSumOfPubkeys
        ? $"{Field(nextCommittee)} aggregate_pubkey is not the aggregate"
        : $"{Field(nextCommittee)} pubkey {Position} ";

    private static SyncCommittee WithKey(SyncCommittee committee, InvalidSyncCommitteeKey key)
    {
        if (key == InvalidSyncCommitteeKey.AggregateNotSumOfPubkeys)
        {
            // A valid key, so only the aggregate relation can refuse it.
            return new SyncCommittee { Pubkeys = committee.Pubkeys, AggregatePubkey = committee.Pubkeys![Position] };
        }

        BlsPublicKey[] pubkeys = [.. committee.Pubkeys!];
        pubkeys[Position] = new BlsPublicKey(Encoding(key));
        return new SyncCommittee { Pubkeys = pubkeys, AggregatePubkey = committee.AggregatePubkey };
    }

    private static byte[] Encoding(InvalidSyncCommitteeKey key)
    {
        switch (key)
        {
            case InvalidSyncCommitteeKey.Infinity:
                return GloasTestFixtures.G1PointAtInfinity();
            default:
                // ethereum/bls12-381-tests deserialization_fails_not_in_G1: on-curve and not infinity, outside the prime-order subgroup.
                return Bytes.FromHexString("0x8123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        }
    }
}

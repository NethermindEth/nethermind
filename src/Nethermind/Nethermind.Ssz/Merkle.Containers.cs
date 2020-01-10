//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Dirichlet.Numerics;
using Chunk = Nethermind.Dirichlet.Numerics.UInt256;

namespace Nethermind.Ssz
{
    public static partial class Merkle
    {
        public static void Ize(out UInt256 root, BlsPublicKey container)
        {
            Ize(out root, container.Bytes);
        }
        
        public static void Ize(out UInt256 root, BlsSignature container)
        {
            Ize(out root, container.Bytes);
        }

        public static void Ize(out UInt256 root, Gwei container)
        {
            Ize(out root, container.Amount);
        }
        
        public static void Ize(out UInt256 root, Slot container)
        {
            Ize(out root, container.Number);
        }
        
        public static void Ize(out UInt256 root, Epoch container)
        {
            Ize(out root, container.Number);
        }
        
        public static void Ize(out UInt256 root, ValidatorIndex container)
        {
            Ize(out root, container.Number);
        }
        
        public static void Ize(out UInt256 root, CommitteeIndex container)
        {
            Ize(out root, container.Number);
        }
        
        public static void Ize(out UInt256 root, Eth1Data? container)
        {
            if (container is null)
            {
                root = RootOfNull;
                return;
            }
            
            Merkleizer merkleizer = new Merkleizer(2);
            merkleizer.Feed(container.DepositRoot);
            merkleizer.Feed(container.DepositCount);
            merkleizer.Feed(container.BlockHash);
            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, DepositData? container)
        {
            if (container is null)
            {
                root = RootOfNull;
                return;
            }
            
            Merkleizer merkleizer = new Merkleizer(2);
            merkleizer.Feed(container.PublicKey);
            merkleizer.Feed(container.WithdrawalCredentials);
            merkleizer.Feed(container.Amount);
            merkleizer.Feed(container.Signature);
            merkleizer.CalculateRoot(out root);
        }

        public static void Ize(out UInt256 root, IList<DepositData> value)
        {
            Merkleizer merkleizer = new Merkleizer(0);
            merkleizer.Feed(value, Ssz.MaximumDepositContracts);
            merkleizer.CalculateRoot(out root);
        }

        public static void Ize(out UInt256 root, AttestationData? container)
        {
            if (container is null)
            {
                root = RootOfNull;
                return;
            }

            Merkleizer merkleizer = new Merkleizer(3);
            merkleizer.Feed(container.Slot);
            merkleizer.Feed(container.Index);
            merkleizer.Feed(container.BeaconBlockRoot);
            merkleizer.Feed(container.Source);
            merkleizer.Feed(container.Target);
            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, BeaconBlockBody? container)
        {
            if (container is null)
            {
                root = RootOfNull;
                return;
            }

            Merkleizer merkleizer = new Merkleizer(3);
            merkleizer.Feed(container.RandaoReveal);
            merkleizer.Feed(container.Eth1Data);
            merkleizer.Feed(container.Graffiti);
            merkleizer.Feed(container.ProposerSlashings, Ssz.MaxProposerSlashings);
            merkleizer.Feed(container.AttesterSlashings, Ssz.MaxAttesterSlashings);
            merkleizer.Feed(container.Attestations, Ssz.MaxAttestations);
            merkleizer.Feed(container.Deposits, Ssz.MaxDeposits);
            merkleizer.Feed(container.VoluntaryExits, Ssz.MaxVoluntaryExits);
            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, BeaconState? container)
        {
            if (container is null)
            {
                root = RootOfNull;
                return;
            }

            Merkleizer merkleizer = new Merkleizer(5);
            merkleizer.Feed(container.GenesisTime);
            merkleizer.Feed(container.Slot);
            merkleizer.Feed(container.Fork);
            merkleizer.Feed(container.LatestBlockHeader);
            merkleizer.Feed(container.BlockRoots);
            merkleizer.Feed(container.StateRoots);
            merkleizer.Feed(container.HistoricalRoots.ToArray(), Ssz.HistoricalRootsLimit);
            merkleizer.Feed(container.Eth1Data);
            merkleizer.Feed(container.Eth1DataVotes.ToArray(), (uint)Ssz.SlotsPerEth1VotingPeriod);
            merkleizer.Feed(container.Eth1DepositIndex);
            merkleizer.Feed(container.Validators, Ssz.ValidatorRegistryLimit);
            merkleizer.Feed(container.Balances.ToArray().ToArray());
            merkleizer.Feed(container.PreviousEpochAttestations, Ssz.MaxAttestations * Ssz.SlotsPerEpoch);
            merkleizer.Feed(container.CurrentEpochAttestations, Ssz.MaxAttestations * Ssz.SlotsPerEpoch);
            merkleizer.FeedBitvector(container.JustificationBits);
            merkleizer.Feed(container.PreviousJustifiedCheckpoint);
            merkleizer.Feed(container.CurrentJustifiedCheckpoint);
            merkleizer.Feed(container.FinalizedCheckpoint);
            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, BeaconBlock? container)
        {
            if (container is null)
            {
                root = RootOfNull;
                return;
            }

            Merkleizer merkleizer = new Merkleizer(3);
            merkleizer.Feed(container.Slot);
            merkleizer.Feed(container.ParentRoot);
            merkleizer.Feed(container.StateRoot);
            merkleizer.Feed(container.Body);
            merkleizer.Feed(container.Signature);
            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, Attestation? container)
        {
            if (container is null)
            {
                root = RootOfNull;
                return;
            }
            
            Merkleizer merkleizer = new Merkleizer(2);
            merkleizer.FeedBitlist(container.AggregationBits, Ssz.MaxValidatorsPerCommittee);
            merkleizer.Feed(container.Data);
            merkleizer.Feed(container.Signature);
            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, IndexedAttestation? container)
        {
            if (container is null)
            {
                root = RootOfNull;
                return;
            }

            Merkleizer merkleizer = new Merkleizer(2);
            merkleizer.Feed(container.AttestingIndices.ToArray(), Ssz.MaxValidatorsPerCommittee);
            merkleizer.Feed(container.Data);
            merkleizer.Feed(container.Signature);
            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, PendingAttestation? container)
        {
            if (container is null)
            {
                root = RootOfNull;
                return;
            }
            
            Merkleizer merkleizer = new Merkleizer(2);
            merkleizer.FeedBitlist(container.AggregationBits, Ssz.MaxValidatorsPerCommittee);
            merkleizer.Feed(container.Data);
            merkleizer.Feed(container.InclusionDelay);
            merkleizer.Feed(container.ProposerIndex);
            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, AttesterSlashing? container)
        {
            if (container is null)
            {
                root = RootOfNull;
                return;
            }
            
            Merkleizer merkleizer = new Merkleizer(1);
            merkleizer.Feed(container.Attestation1);
            merkleizer.Feed(container.Attestation2);
            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, Deposit? container)
        {
            if (container is null)
            {
                root = RootOfNull;
                return;
            }
            
            Merkleizer merkleizer = new Merkleizer(1);
            merkleizer.Feed(container.Proof);
            merkleizer.Feed(container.Data);
            merkleizer.CalculateRoot(out root);
        }

        private static UInt256 RootOfNull;

        public static void Ize(out UInt256 root, ProposerSlashing? container)
        {
            if (container is null)
            {
                root = RootOfNull;
                return;
            }
            
            Merkleizer merkleizer = new Merkleizer(2);
            merkleizer.Feed(container.ProposerIndex);
            merkleizer.Feed(container.Header1);
            merkleizer.Feed(container.Header2);
            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, Fork? container)
        {
            if (container is null)
            {
                root = RootOfNull;
                return;
            }
            
            Merkleizer merkleizer = new Merkleizer(2);
            merkleizer.Feed(container.Value.PreviousVersion);
            merkleizer.Feed(container.Value.CurrentVersion);
            merkleizer.Feed(container.Value.Epoch);
            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, Checkpoint? container)
        {
            if (container is null)
            {
                root = RootOfNull;
                return;
            }
            
            Merkleizer merkleizer = new Merkleizer(1);
            merkleizer.Feed(container.Value.Epoch);
            merkleizer.Feed(container.Value.Root);
            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, HistoricalBatch? container)
        {
            if (container is null)
            {
                root = RootOfNull;
                return;
            }
            
            Merkleizer merkleizer = new Merkleizer(1);
            merkleizer.Feed(container.BlockRoots);
            merkleizer.Feed(container.StateRoots);
            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, VoluntaryExit? container)
        {
            if (container is null)
            {
                root = RootOfNull;
                return;
            }
            
            Merkleizer merkleizer = new Merkleizer(2);
            merkleizer.Feed(container.Epoch);
            merkleizer.Feed(container.ValidatorIndex);
            merkleizer.Feed(container.Signature);
            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, Validator? container)
        {
            if (container is null)
            {
                root = RootOfNull;
                return;
            }
            
            Merkleizer merkleizer = new Merkleizer(3);
            merkleizer.Feed(container.PublicKey);
            merkleizer.Feed(container.WithdrawalCredentials);
            merkleizer.Feed(container.EffectiveBalance);
            merkleizer.Feed(container.IsSlashed);
            merkleizer.Feed(container.ActivationEligibilityEpoch);
            merkleizer.Feed(container.ActivationEpoch);
            merkleizer.Feed(container.ExitEpoch);
            merkleizer.Feed(container.WithdrawableEpoch);
            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, BeaconBlockHeader? container)
        {
            if (container is null)
            {
                root = RootOfNull;
                return;
            }
            
            Merkleizer merkleizer = new Merkleizer(3);
            merkleizer.Feed(container.Slot);
            merkleizer.Feed(container.ParentRoot);
            merkleizer.Feed(container.StateRoot);
            merkleizer.Feed(container.BodyRoot);
            merkleizer.Feed(container.Signature);
            merkleizer.CalculateRoot(out root);
        }
    }
}
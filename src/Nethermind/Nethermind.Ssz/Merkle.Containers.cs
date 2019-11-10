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

using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Dirichlet.Numerics;
using NLog.StructuredLogging.Json.Helpers;
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
        
        public static void Ize(out UInt256 root, Eth1Data container)
        {
            Merkleizer merkleizer = new Merkleizer(2);
            merkleizer.Feed(container.DepositRoot);
            merkleizer.Feed(container.DepositCount);
            merkleizer.Feed(container.BlockHash);
            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, DepositData container)
        {
            Merkleizer merkleizer = new Merkleizer(2);
            merkleizer.Feed(container.PublicKey);
            merkleizer.Feed(container.WithdrawalCredentials);
            merkleizer.Feed(container.Amount);
            merkleizer.Feed(container.Signature);
            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, AttestationData container)
        {
            Merkleizer merkleizer = new Merkleizer(3);
            merkleizer.Feed(container.Slot);
            merkleizer.Feed(container.CommitteeIndex);
            merkleizer.Feed(container.BeaconBlockRoot);
            merkleizer.Feed(container.Source);
            merkleizer.Feed(container.Target);
            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, Attestation container)
        {
            Merkleizer merkleizer = new Merkleizer(3);
            merkleizer.Feed(container.AggregationBits);
            merkleizer.Feed(container.Data);
            merkleizer.Feed(container.CustodyBits);
            merkleizer.Feed(container.Signature);
            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, ProposerSlashing container)
        {
            Merkleizer merkleizer = new Merkleizer(2);
            merkleizer.Feed(container.ProposerIndex);
            merkleizer.Feed(container.Header1);
            merkleizer.Feed(container.Header2);
            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, Fork container)
        {
            Merkleizer merkleizer = new Merkleizer(2);
            merkleizer.Feed(container.PreviousVersion);
            merkleizer.Feed(container.CurrentVersion);
            merkleizer.Feed(container.Epoch);
            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, Checkpoint container)
        {
            Merkleizer merkleizer = new Merkleizer(1);
            merkleizer.Feed(container.Epoch);
            merkleizer.Feed(container.Root);
            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, HistoricalBatch container)
        {
            Merkleizer merkleizer = new Merkleizer(1);
            merkleizer.Feed(container.BlockRoots);
            merkleizer.Feed(container.StateRoots);
            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, VoluntaryExit container)
        {
            Merkleizer merkleizer = new Merkleizer(2);
            merkleizer.Feed(container.Epoch);
            merkleizer.Feed(container.ValidatorIndex);
            merkleizer.Feed(container.Signature);
            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, Validator container)
        {
            Merkleizer merkleizer = new Merkleizer(3);
            merkleizer.Feed(container.PublicKey);
            merkleizer.Feed(container.WithdrawalCredentials);
            merkleizer.Feed(container.EffectiveBalance);
            merkleizer.Feed(container.Slashed);
            merkleizer.Feed(container.ActivationEligibilityEpoch);
            merkleizer.Feed(container.ActivationEpoch);
            merkleizer.Feed(container.ExitEpoch);
            merkleizer.Feed(container.WithdrawableEpoch);
            merkleizer.CalculateRoot(out root);
        }
        
        public static void Ize(out UInt256 root, BeaconBlockHeader container)
        {
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
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
using System.Collections.Generic;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Hash32 = Nethermind.Core2.Crypto.Hash32;

namespace Nethermind.Core2
{
    public interface ICryptographyService
    {
        BlsPublicKey BlsAggregatePublicKeys(IEnumerable<BlsPublicKey> publicKeys);

        bool BlsVerify(BlsPublicKey publicKey, Hash32 signingRoot, BlsSignature signature, Domain domain);

        bool BlsVerifyMultiple(IEnumerable<BlsPublicKey> publicKeys, IEnumerable<Hash32> messageHashes, BlsSignature signature, Domain domain);

        Hash32 Hash(Hash32 a, Hash32 b);

        Hash32 Hash(ReadOnlySpan<byte> bytes);

        Hash32 HashTreeRoot(AttestationData attestationData);
        Hash32 HashTreeRoot(BeaconBlock beaconBlock);
        Hash32 HashTreeRoot(BeaconBlockBody beaconBlockBody);
        Hash32 HashTreeRoot(BeaconState beaconState);
        Hash32 HashTreeRoot(DepositData depositData);
        Hash32 HashTreeRoot(IList<DepositData> depositData);
        Hash32 HashTreeRoot(Epoch epoch);
        Hash32 HashTreeRoot(HistoricalBatch historicalBatch);

        Hash32 SigningRoot(BeaconBlock beaconBlock);
        Hash32 SigningRoot(BeaconBlockHeader beaconBlockHeader);
        Hash32 SigningRoot(DepositData depositData);
        Hash32 SigningRoot(VoluntaryExit voluntaryExit);
    }
}

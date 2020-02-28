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

        bool BlsVerify(BlsPublicKey publicKey, Root signingRoot, BlsSignature signature, Domain domain);

        bool BlsVerifyMultiple(IEnumerable<BlsPublicKey> publicKeys, IEnumerable<Root> messageHashes, BlsSignature signature, Domain domain);

        Root Hash(Root a, Root b);

        Root Hash(ReadOnlySpan<byte> bytes);

        Root HashTreeRoot(AttestationData attestationData);
        Root HashTreeRoot(BeaconBlock beaconBlock);
        Root HashTreeRoot(BeaconBlockBody beaconBlockBody);
        Root HashTreeRoot(BeaconState beaconState);
        Root HashTreeRoot(DepositData depositData);
        Root HashTreeRoot(IList<DepositData> depositData);
        Root HashTreeRoot(Epoch epoch);
        Root HashTreeRoot(HistoricalBatch historicalBatch);
        Root HashTreeRoot(VoluntaryExit voluntaryExit);
    }
}

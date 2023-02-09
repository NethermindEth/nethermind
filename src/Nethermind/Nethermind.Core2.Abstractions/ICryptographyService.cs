// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2
{
    public interface ICryptographyService
    {
        BlsPublicKey BlsAggregatePublicKeys(IList<BlsPublicKey> publicKeys);
        bool BlsAggregateVerify(IList<BlsPublicKey> publicKeys, IList<Root> signingRoots, BlsSignature signature);
        bool BlsFastAggregateVerify(IList<BlsPublicKey> publicKey, Root signingRoot, BlsSignature signature);
        bool BlsVerify(BlsPublicKey publicKey, Root signingRoot, BlsSignature signature);
        Bytes32 Hash(Bytes32 a, Bytes32 b);
        Bytes32 Hash(ReadOnlySpan<byte> bytes);
        Root HashTreeRoot(AttestationData attestationData);
        Root HashTreeRoot(BeaconBlock beaconBlock);
        Root HashTreeRoot(BeaconBlockBody beaconBlockBody);
        Root HashTreeRoot(BeaconBlockHeader beaconBlockHeader);
        Root HashTreeRoot(BeaconState beaconState);
        Root HashTreeRoot(DepositData depositData);

        public Root HashTreeRoot(Ref<DepositData> depositData)
        {
            return depositData.Root ?? (depositData.Root = HashTreeRoot(depositData.Item));
        }

        Root HashTreeRoot(DepositMessage depositMessage);
        Root HashTreeRoot(List<Ref<DepositData>> depositData);
        Root HashTreeRoot(List<DepositData> depositData);
        Root HashTreeRoot(Epoch epoch);
        Root HashTreeRoot(HistoricalBatch historicalBatch);
        Root HashTreeRoot(SigningRoot signingRoot);
        Root HashTreeRoot(VoluntaryExit voluntaryExit);
    }
}

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
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nethermind.Core2.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Ssz;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Hash32 = Nethermind.Core2.Types.Hash32;

namespace Nethermind.BeaconNode.Test.Helpers
{
    public static class TestDeposit
    {
        public static (Deposit, Hash32) BuildDeposit(IServiceProvider testServiceProvider, BeaconState? state, IList<DepositData> depositDataList, BlsPublicKey publicKey, byte[] privateKey, Gwei amount, Hash32 withdrawalCredentials, bool signed)
        {
            var chainConstants = testServiceProvider.GetService<ChainConstants>();
            var beaconChainUtility = testServiceProvider.GetService<BeaconChainUtility>();

            var depositData = BuildDepositData(testServiceProvider, publicKey, privateKey, amount, withdrawalCredentials, state, signed);
            var index = depositDataList.Count;
            depositDataList.Add(depositData);
            Hash32 root = depositDataList.HashTreeRoot((ulong)1 << chainConstants.DepositContractTreeDepth);
            var allLeaves = depositDataList.Select(x => x.HashTreeRoot());
            var tree = TestSecurity.CalculateMerkleTreeFromLeaves(allLeaves);
            var merkleProof = TestSecurity.GetMerkleProof(tree, index, 32);
            var proof = new List<Hash32>(merkleProof);
            var indexBytes = new Span<byte>(new byte[32]);
            BitConverter.TryWriteBytes(indexBytes, (ulong)index + 1);
            if (!BitConverter.IsLittleEndian)
            {
                indexBytes.Slice(0, 8).Reverse();
            }
            var indexHash = new Hash32(indexBytes);
            proof.Add(indexHash);
            var leaf = depositData.HashTreeRoot();
            beaconChainUtility.IsValidMerkleBranch(leaf, proof, chainConstants.DepositContractTreeDepth + 1, (ulong)index, root);
            var deposit = new Deposit(proof, depositData);
            return (deposit, root);
        }

        public static DepositData BuildDepositData(IServiceProvider testServiceProvider, BlsPublicKey publicKey, byte[] privateKey, Gwei amount, Hash32 withdrawalCredentials, BeaconState? state, bool signed)
        {
            var depositData = new DepositData(publicKey, withdrawalCredentials, amount);
            if (signed)
            {
                SignDepositData(testServiceProvider, depositData, privateKey, state);
            }
            return depositData;
        }

        public static (IEnumerable<Deposit>, Hash32) PrepareGenesisDeposits(IServiceProvider testServiceProvider, int genesisValidatorCount, Gwei amount, bool signed)
        {
            var chainConstants = testServiceProvider.GetService<ChainConstants>();
            var miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            var initialValues = testServiceProvider.GetService<IOptions<InitialValues>>().Value;
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            var maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;

            var beaconChainUtility = testServiceProvider.GetService<BeaconChainUtility>();
            var beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();
            var beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

            var privateKeys = TestKeys.PrivateKeys(timeParameters).ToArray();
            BlsPublicKey[] publicKeys;
            if (signed)
            {
                publicKeys = TestKeys.PublicKeys(timeParameters).ToArray();
            }
            else
            {
                publicKeys = privateKeys.Select(x => new BlsPublicKey(x)).ToArray();
            }
            var depositDataList = new List<DepositData>();
            var genesisDeposits = new List<Deposit>();
            var root = Hash32.Zero;
            for (var validatorIndex = 0; validatorIndex < genesisValidatorCount; validatorIndex++)
            {
                var publicKey = publicKeys[validatorIndex];
                var privateKey = privateKeys[validatorIndex];
                // insecurely use pubkey as withdrawal key if no credentials provided
                var withdrawalCredentialBytes = TestSecurity.Hash(publicKey.AsSpan());
                withdrawalCredentialBytes[0] = initialValues.BlsWithdrawalPrefix;
                var withdrawalCredentials = new Hash32(withdrawalCredentialBytes);
                (var deposit, var depositRoot) = BuildDeposit(testServiceProvider, null, depositDataList, publicKey, privateKey, amount, withdrawalCredentials, signed);
                root = depositRoot;
                genesisDeposits.Add(deposit);
            }
            return (genesisDeposits, root);
        }

        /// <summary>
        /// Prepare the state for the deposit, and create a deposit for the given validator, depositing the given amount.
        /// </summary>
        public static Deposit PrepareStateAndDeposit(IServiceProvider testServiceProvider, BeaconState state, ValidatorIndex validatorIndex, Gwei amount, Hash32 withdrawalCredentials, bool signed)
        {
            var chainConstants = testServiceProvider.GetService<ChainConstants>();
            var initialValues = testServiceProvider.GetService<IOptions<InitialValues>>().Value;
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;

            var beaconChainUtility = testServiceProvider.GetService<BeaconChainUtility>();
            var beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            var privateKeys = TestKeys.PrivateKeys(timeParameters).ToArray();
            var publicKeys = TestKeys.PublicKeys(timeParameters).ToArray();
            var privateKey = privateKeys[(int)(ulong)validatorIndex];
            var publicKey = publicKeys[(int)(ulong)validatorIndex];

            if (withdrawalCredentials == Hash32.Zero)
            {
                // insecurely use pubkey as withdrawal key if no credentials provided
                var withdrawalCredentialBytes = TestSecurity.Hash(publicKey.AsSpan());
                withdrawalCredentialBytes[0] = initialValues.BlsWithdrawalPrefix;
                withdrawalCredentials = new Hash32(withdrawalCredentialBytes);
            }

            var depositDataList = new List<DepositData>();
            (var deposit, var depositRoot) = BuildDeposit(testServiceProvider, state, depositDataList, publicKey, privateKey, amount, withdrawalCredentials, signed);

            state.SetEth1DepositIndex(0);
            state.Eth1Data.SetDepositRoot(depositRoot);
            state.Eth1Data.SetDepositCount((ulong)depositDataList.Count);

            return deposit;
        }

        public static void SignDepositData(IServiceProvider testServiceProvider, DepositData depositData, byte[] privateKey, BeaconState? state)
        {
            var signatureDomains = testServiceProvider.GetService<IOptions<SignatureDomains>>().Value;

            Domain domain;
            if (state == null)
            {
                // Genesis
                var beaconChainUtility = testServiceProvider.GetService<BeaconChainUtility>();
                domain = beaconChainUtility.ComputeDomain(signatureDomains.Deposit);
            }
            else
            {
                var beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();
                domain = beaconStateAccessor.GetDomain(state, signatureDomains.Deposit, Epoch.None);
            }

            var signature = TestSecurity.BlsSign(depositData.SigningRoot(), privateKey, domain);
            depositData.SetSignature(signature);
        }
    }
}

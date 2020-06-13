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
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
namespace Nethermind.BeaconNode.Test.Helpers
{
    public static class TestDeposit
    {
        public static (Deposit, Root) BuildDeposit(IServiceProvider testServiceProvider, BeaconState? state, List<DepositData> depositDataList, BlsPublicKey publicKey, byte[] privateKey, Gwei amount, Bytes32 withdrawalCredentials, bool signed)
        {
            ChainConstants chainConstants = testServiceProvider.GetService<ChainConstants>();
            IBeaconChainUtility beaconChainUtility = testServiceProvider.GetService<IBeaconChainUtility>();
            ICryptographyService cryptographyService = testServiceProvider.GetService<ICryptographyService>();

            DepositData depositData = BuildDepositData(testServiceProvider, publicKey, privateKey, amount, withdrawalCredentials, state, signed);
            int index = depositDataList.Count;
            depositDataList.Add(depositData);
            Root root = cryptographyService.HashTreeRoot(depositDataList);
            IEnumerable<Bytes32> allLeaves = depositDataList.Select(x => new Bytes32(cryptographyService.HashTreeRoot(x).AsSpan()));
            IList<IList<Bytes32>> tree = TestSecurity.CalculateMerkleTreeFromLeaves(allLeaves);
            IList<Bytes32> merkleProof = TestSecurity.GetMerkleProof(tree, index, 32);
            List<Bytes32> proof = new List<Bytes32>(merkleProof);
            Span<byte> indexBytes = new Span<byte>(new byte[32]);
            BitConverter.TryWriteBytes(indexBytes, (ulong)index + 1);
            if (!BitConverter.IsLittleEndian)
            {
                indexBytes.Slice(0, 8).Reverse();
            }
            
            Bytes32 indexHash = new Bytes32(indexBytes);
            proof.Add(indexHash);
            Bytes32 leaf = new Bytes32(cryptographyService.HashTreeRoot(depositData).AsSpan());
            bool checkValid = beaconChainUtility.IsValidMerkleBranch(leaf, proof, chainConstants.DepositContractTreeDepth + 1, (ulong)index, root);
            if (!checkValid)
            {
                throw new Exception($"Invalid Merkle branch for deposit for validator public key {depositData.PublicKey}");
            }
            
            Deposit deposit = new Deposit(proof, depositData.OrRoot);
            return (deposit, root);
        }

        public static DepositData BuildDepositData(IServiceProvider testServiceProvider, BlsPublicKey publicKey, byte[] privateKey, Gwei amount, Bytes32 withdrawalCredentials, BeaconState? state, bool signed)
        {
            DepositData depositData = new DepositData(publicKey, withdrawalCredentials, amount, BlsSignature.Zero);
            if (signed)
            {
                SignDepositData(testServiceProvider, depositData, privateKey, state);
            }
            return depositData;
        }

        public static (IList<Deposit>, Root) PrepareGenesisDeposits(IServiceProvider testServiceProvider, int genesisValidatorCount, Gwei amount, bool signed)
        {
            ChainConstants chainConstants = testServiceProvider.GetService<ChainConstants>();
            MiscellaneousParameters miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            InitialValues initialValues = testServiceProvider.GetService<IOptions<InitialValues>>().Value;
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            MaxOperationsPerBlock maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;

            IBeaconChainUtility beaconChainUtility = testServiceProvider.GetService<IBeaconChainUtility>();
            BeaconStateAccessor beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();
            BeaconStateTransition beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

            byte[][] privateKeys = TestKeys.PrivateKeys(timeParameters).ToArray();
            BlsPublicKey[] publicKeys;
            if (signed)
            {
                publicKeys = TestKeys.PublicKeys(timeParameters).ToArray();
            }
            else
            {
                publicKeys = privateKeys.Select(x => new BlsPublicKey(x)).ToArray();
            }
            List<DepositData> depositDataList = new List<DepositData>();
            List<Deposit> genesisDeposits = new List<Deposit>();
            Root root = Root.Zero;
            for (int validatorIndex = 0; validatorIndex < genesisValidatorCount; validatorIndex++)
            {
                BlsPublicKey publicKey = publicKeys[validatorIndex];
                byte[] privateKey = privateKeys[validatorIndex];
                // insecurely use pubkey as withdrawal key if no credentials provided
                byte[] withdrawalCredentialBytes = TestSecurity.Hash(publicKey.AsSpan());
                withdrawalCredentialBytes[0] = initialValues.BlsWithdrawalPrefix;
                Bytes32 withdrawalCredentials = new Bytes32(withdrawalCredentialBytes);
                (Deposit deposit, Root depositRoot) = BuildDeposit(testServiceProvider, null, depositDataList, publicKey, privateKey, amount, withdrawalCredentials, signed);
                root = depositRoot;
                genesisDeposits.Add(deposit);
            }
            return (genesisDeposits, root);
        }

        /// <summary>
        /// Prepare the state for the deposit, and create a deposit for the given validator, depositing the given amount.
        /// </summary>
        public static Deposit PrepareStateAndDeposit(IServiceProvider testServiceProvider, BeaconState state, ValidatorIndex validatorIndex, Gwei amount, Bytes32 withdrawalCredentials, bool signed)
        {
            ChainConstants chainConstants = testServiceProvider.GetService<ChainConstants>();
            InitialValues initialValues = testServiceProvider.GetService<IOptions<InitialValues>>().Value;
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;

            IBeaconChainUtility beaconChainUtility = testServiceProvider.GetService<IBeaconChainUtility>();
            BeaconStateAccessor beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            byte[][] privateKeys = TestKeys.PrivateKeys(timeParameters).ToArray();
            BlsPublicKey[] publicKeys = TestKeys.PublicKeys(timeParameters).ToArray();
            byte[] privateKey = privateKeys[(int)(ulong)validatorIndex];
            BlsPublicKey publicKey = publicKeys[(int)(ulong)validatorIndex];

            if (withdrawalCredentials == Bytes32.Zero)
            {
                // insecurely use pubkey as withdrawal key if no credentials provided
                byte[] withdrawalCredentialBytes = TestSecurity.Hash(publicKey.AsSpan());
                withdrawalCredentialBytes[0] = initialValues.BlsWithdrawalPrefix;
                withdrawalCredentials = new Bytes32(withdrawalCredentialBytes);
            }

            List<DepositData> depositDataList = new List<DepositData>();
            (Deposit deposit, Root depositRoot) = BuildDeposit(testServiceProvider, state, depositDataList, publicKey, privateKey, amount, withdrawalCredentials, signed);

            state.SetEth1DepositIndex(0);
            state.Eth1Data.SetDepositRoot(depositRoot);
            state.Eth1Data.SetDepositCount((ulong)depositDataList.Count);

            return deposit;
        }

        public static void SignDepositData(IServiceProvider testServiceProvider, DepositData depositData, byte[] privateKey, BeaconState? state)
        {
            SignatureDomains signatureDomains = testServiceProvider.GetService<IOptions<SignatureDomains>>().Value;
            IBeaconChainUtility beaconChainUtility = testServiceProvider.GetService<IBeaconChainUtility>();
            ICryptographyService cryptographyService = testServiceProvider.GetService<ICryptographyService>();

            Domain domain;
            if (state == null)
            {
                // Genesis
                domain = beaconChainUtility.ComputeDomain(signatureDomains.Deposit);
            }
            else
            {
                BeaconStateAccessor beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();
                domain = beaconStateAccessor.GetDomain(state, signatureDomains.Deposit, Epoch.None);
            }

            DepositMessage depositMessage = new DepositMessage(depositData.PublicKey, depositData.WithdrawalCredentials, depositData.Amount);
            Root depositMessageRoot = cryptographyService.HashTreeRoot(depositMessage);
            Root signingRoot = beaconChainUtility.ComputeSigningRoot(depositMessageRoot, domain);
            BlsSignature signature = TestSecurity.BlsSign(signingRoot, privateKey);
            depositData.SetSignature(signature);
        }
    }
}

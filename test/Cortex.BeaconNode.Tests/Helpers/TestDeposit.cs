using System;
using System.Collections.Generic;
using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Ssz;
using Cortex.Containers;

namespace Cortex.BeaconNode.Tests.Helpers
{
    public static class TestDeposit
    {
        public static (Deposit, Hash32) BuildDeposit(BeaconState? state, IList<DepositData> depositDataList, BlsPublicKey publicKey, byte[] privateKey, Gwei amount, Hash32 withdrawalCredentials, bool signed,
            ChainConstants chainConstants, BeaconChainUtility beaconChainUtility, BeaconStateAccessor beaconStateAccessor)
        {
            var depositData = BuildDepositData(publicKey, privateKey, amount, withdrawalCredentials, state, signed,
                beaconChainUtility, beaconStateAccessor);
            var index = depositDataList.Count;
            depositDataList.Add(depositData);
            Hash32 root = depositDataList.HashTreeRoot((ulong)1 << chainConstants.DepositContractTreeDepth);
            var allLeaves = depositDataList.Select(x => x.HashTreeRoot());
            var tree = TestUtility.CalculateMerkleTreeFromLeaves(allLeaves);
            var merkleProof = TestUtility.GetMerkleProof(tree, index, 32);
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

        public static DepositData BuildDepositData(BlsPublicKey publicKey, byte[] privateKey, Gwei amount, Hash32 withdrawalCredentials, BeaconState? state, bool signed,
            BeaconChainUtility beaconChainUtility, BeaconStateAccessor beaconStateAccessor)
        {
            var depositData = new DepositData(publicKey, withdrawalCredentials, amount);
            if (signed)
            {
                SignDepositData(depositData, privateKey, state, beaconChainUtility, beaconStateAccessor);
            }
            return depositData;
        }

        public static (IEnumerable<Deposit>, Hash32) PrepareGenesisDeposits(int genesisValidatorCount, Gwei amount, bool signed,
            ChainConstants chainConstants, InitialValues initialValues, TimeParameters timeParameters,
            BeaconChainUtility beaconChainUtility, BeaconStateAccessor beaconStateAccessor)
        {
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
            Hash32 root = Hash32.Zero;
            for (var validatorIndex = 0; validatorIndex < genesisValidatorCount; validatorIndex++)
            {
                var publicKey = publicKeys[validatorIndex];
                var privateKey = privateKeys[validatorIndex];
                // insecurely use pubkey as withdrawal key if no credentials provided
                var withdrawalCredentialBytes = TestUtility.Hash(publicKey.AsSpan());
                withdrawalCredentialBytes[0] = initialValues.BlsWithdrawalPrefix;
                var withdrawalCredentials = new Hash32(withdrawalCredentialBytes);
                (var deposit, var depositRoot) = BuildDeposit(null, depositDataList, publicKey, privateKey, amount, withdrawalCredentials, signed,
                    chainConstants, beaconChainUtility, beaconStateAccessor);
                root = depositRoot;
                genesisDeposits.Add(deposit);
            }
            return (genesisDeposits, root);
        }

        /// <summary>
        /// Prepare the state for the deposit, and create a deposit for the given validator, depositing the given amount.
        /// </summary>
        public static Deposit PrepareStateAndDeposit(BeaconState state, ValidatorIndex validatorIndex, Gwei amount, Hash32 withdrawalCredentials, bool signed,
            ChainConstants chainConstants, InitialValues initialValues, TimeParameters timeParameters,
            BeaconChainUtility beaconChainUtility, BeaconStateAccessor beaconStateAccessor)
        {
            var privateKeys = TestKeys.PrivateKeys(timeParameters).ToArray();
            var publicKeys = TestKeys.PublicKeys(timeParameters).ToArray();
            var privateKey = privateKeys[(int)(ulong)validatorIndex];
            var publicKey = publicKeys[(int)(ulong)validatorIndex];

            if (withdrawalCredentials == Hash32.Zero)
            {
                // insecurely use pubkey as withdrawal key if no credentials provided
                var withdrawalCredentialBytes = TestUtility.Hash(publicKey.AsSpan());
                withdrawalCredentialBytes[0] = initialValues.BlsWithdrawalPrefix;
                withdrawalCredentials = new Hash32(withdrawalCredentialBytes);
            }

            var depositDataList = new List<DepositData>();
            (var deposit, var depositRoot) = BuildDeposit(state, depositDataList, publicKey, privateKey, amount, withdrawalCredentials, signed,
                chainConstants,
                beaconChainUtility, beaconStateAccessor);

            state.SetEth1DepositIndex(0);
            state.Eth1Data.SetDepositRoot(depositRoot);
            state.Eth1Data.SetDepositCount((ulong)depositDataList.Count);

            return deposit;
        }

        public static void SignDepositData(DepositData depositData, byte[] privateKey, BeaconState? state,
            BeaconChainUtility beaconChainUtility, BeaconStateAccessor beaconStateAccessor)
        {
            Domain domain;
            if (state == null)
            {
                // Genesis
                domain = beaconChainUtility.ComputeDomain(DomainType.Deposit);
            }
            else
            {
                domain = beaconStateAccessor.GetDomain(state, DomainType.Deposit, Epoch.None);
            }

            var signature = TestUtility.BlsSign(depositData.SigningRoot(), privateKey, domain);
            depositData.SetSignature(signature);
        }
    }
}

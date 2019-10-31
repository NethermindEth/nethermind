using System;
using System.Collections.Generic;
using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Ssz;
using Cortex.BeaconNode.Tests.Helpers;
using Cortex.Containers;
using Cortex.Cryptography;

namespace Cortex.BeaconNode.Tests
{
    public static class TestData
    {
        public static (Deposit, Hash32) BuildDeposit(
            ChainConstants chainConstants, BeaconChainUtility beaconChainUtility, BeaconState? state,
            IList<DepositData> depositDataList, BlsPublicKey publicKey, byte[] privateKey, Gwei amount,
            Hash32 withdrawalCredentials, bool signed)
        {
            var depositData = BuildDepositData(beaconChainUtility, publicKey, privateKey, amount, withdrawalCredentials, state, signed);
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

        public static DepositData BuildDepositData(BeaconChainUtility beaconChainUtility, BlsPublicKey publicKey,
            byte[] privateKey, Gwei amount, Hash32 withdrawalCredentials, BeaconState? state = null, bool signed = false)
        {
            var depositData = new DepositData(publicKey, withdrawalCredentials, amount);
            if (signed)
            {
                SignDepositData(beaconChainUtility, depositData, privateKey, state);
            }
            return depositData;
        }

        public static IList<Checkpoint> GetCheckpoints(Epoch epoch)
        {
            var checkpoints = new List<Checkpoint>();
            if (epoch >= new Epoch(1))
            {
                checkpoints.Add(new Checkpoint(new Epoch((ulong)epoch - 1), new Hash32(Enumerable.Repeat((byte)0xaa, 32).ToArray())));
            }
            if (epoch >= new Epoch(2))
            {
                checkpoints.Add(new Checkpoint(new Epoch((ulong)epoch - 2), new Hash32(Enumerable.Repeat((byte)0xbb, 32).ToArray())));
            }
            if (epoch >= new Epoch(3))
            {
                checkpoints.Add(new Checkpoint(new Epoch((ulong)epoch - 3), new Hash32(Enumerable.Repeat((byte)0xcc, 32).ToArray())));
            }
            if (epoch >= new Epoch(4))
            {
                checkpoints.Add(new Checkpoint(new Epoch((ulong)epoch - 4), new Hash32(Enumerable.Repeat((byte)0xdd, 32).ToArray())));
            }
            if (epoch >= new Epoch(5))
            {
                checkpoints.Add(new Checkpoint(new Epoch((ulong)epoch - 5), new Hash32(Enumerable.Repeat((byte)0xee, 32).ToArray())));
            }
            return checkpoints;
        }

        public static (IEnumerable<Deposit>, Hash32) PrepareGenesisDeposits(ChainConstants chainConstants, InitialValues initialValues,
            TimeParameters timeParameters, BeaconChainUtility beaconChainUtility, int genesisValidatorCount, Gwei amount,
            bool signed)
        {
            var privateKeys = TestKeys.PrivateKeys(timeParameters).ToArray();
            BlsPublicKey[] publicKeys;
            if (signed)
            {
                publicKeys = TestKeys.PublicKeys(privateKeys).ToArray();
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
                (var deposit, var depositRoot) = BuildDeposit(chainConstants, beaconChainUtility, null, depositDataList, publicKey, privateKey, amount, withdrawalCredentials, signed);
                root = depositRoot;
                genesisDeposits.Add(deposit);
            }
            return (genesisDeposits, root);
        }


        public static void SignDepositData(BeaconChainUtility beaconChainUtility, DepositData depositData, byte[] privateKey, BeaconState? state = null)
        {
            Domain domain;
            if (state == null)
            {
                // Genesis
                domain = beaconChainUtility.ComputeDomain(DomainType.Deposit);
            }
            else
            {
                //domain = spec.get_domain(
                //            state,
                //            spec.DOMAIN_DEPOSIT,
                //        )
                throw new NotImplementedException();
            }

            var signature = TestUtility.BlsSign(depositData.SigningRoot(), privateKey, domain);
            depositData.SetSignature(signature);
        }
    }
}

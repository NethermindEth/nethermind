using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Cortex.BeaconNode.Ssz;
using Cortex.Containers;
using Cortex.Cryptography;

namespace Cortex.BeaconNode.Tests
{
    public static class TestData
    {
        private const int DEPOSIT_CONTRACT_TREE_DEPTH = 1 << 5; // 32
        private const int SLOTS_PER_EPOCH = 8;
        private const byte BLS_WITHDRAWAL_PREFIX = 0x00;

        public static IEnumerable<byte[]> PrivateKeys()
        {
            // Private key is 381 bits (fit in 48 bytes = 384 bits; is 6 ulongs)
            // Number as little endian (1, 2, 3, ...)
            var privateKeys = Enumerable.Range(0, SLOTS_PER_EPOCH * 16).Select(x => {
                var key = new byte[48];
                var bytes = BitConverter.GetBytes((ulong)(x + 1));
                bytes.CopyTo(key, 0);
                return key;
                });
            return privateKeys;
        }

        private static IEnumerable<BlsPublicKey> PublicKeys(IEnumerable<byte[]> privateKeys)
        {
            return privateKeys.Select(x =>
            {
                var blsParameters = new BLSParameters() 
                {
                    PrivateKey = x
                };
                using var bls = BLS.Create(blsParameters);
                var bytes = new Span<byte>(new byte[BlsPublicKey.Length]);
                bls.TryExportBLSPublicKey(bytes, out var bytesWritten);
                return new BlsPublicKey(bytes);
            });
        }

        public static (IEnumerable<Deposit>, Hash32) PrepareGenesisDeposits(BeaconChainUtility beaconChainUtility, int genesisValidatorCount, Gwei amount, bool signed)
        {
            var privateKeys = PrivateKeys().ToArray();
            BlsPublicKey[] publicKeys;
            if (signed)
            {
                publicKeys = PublicKeys(privateKeys).ToArray();
            } 
            else
            {
                publicKeys = Enumerable.Repeat(new BlsPublicKey(), privateKeys.Length).ToArray();
            }
            var depositDataList = new List<DepositData>();
            var genesisDeposits = new List<Deposit>();
            Hash32 root = new Hash32();
            for (var validatorIndex = 0; validatorIndex < genesisValidatorCount; validatorIndex++)
            {
                var publicKey = publicKeys[validatorIndex];
                var privateKey = privateKeys[validatorIndex];
                // insecurely use pubkey as withdrawal key if no credentials provided
                var withdrawalCredentialBytes = TestUtility.Hash(publicKey);
                withdrawalCredentialBytes[0] = BLS_WITHDRAWAL_PREFIX;
                var withdrawalCredentials = new Hash32(withdrawalCredentialBytes);
                (var deposit, var depositRoot) = BuildDeposit(beaconChainUtility, null, depositDataList, publicKey, privateKey, amount, withdrawalCredentials, signed);
                root = depositRoot;
                genesisDeposits.Add(deposit);
            }
            return (genesisDeposits, root);
        }

        public static (Deposit, Hash32) BuildDeposit(BeaconChainUtility beaconChainUtility, BeaconState? state, 
            IList<DepositData> depositDataList,
            BlsPublicKey publicKey,
            byte[] privateKey, Gwei amount, Hash32 withdrawalCredentials, bool signed)
        {
            var depositData = BuildDepositData(beaconChainUtility, publicKey, privateKey, amount, withdrawalCredentials, state, signed);
            var index = depositDataList.Count;
            depositDataList.Add(depositData);
            Hash32 root = depositDataList.HashTreeRoot((ulong)1 << DEPOSIT_CONTRACT_TREE_DEPTH);
            var allLeaves = depositDataList.Select(x => new Hash32(x.HashTreeRoot()));
            var tree = TestUtility.CalculateMerkleTreeFromLeaves(allLeaves);
            var merkleProof = TestUtility.GetMerkleProof(tree, index, 32);
            var proof = new List<Hash32>(merkleProof);
            var indexBytes = BitConverter.GetBytes((ulong)index + 1);
            if (!BitConverter.IsLittleEndian)
            {
                indexBytes = indexBytes.Reverse().ToArray();
            }
            var indexHash = new byte[32];
            indexBytes.CopyTo(indexHash, 0);
            proof.Add(indexHash);
            var leaf = depositData.HashTreeRoot();
            beaconChainUtility.IsValidMerkleBranch(leaf, proof, DEPOSIT_CONTRACT_TREE_DEPTH + 1, (ulong)index, root);
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

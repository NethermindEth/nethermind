using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Ssz;
using Cortex.Containers;
using Cortex.Cryptography;

namespace Cortex.BeaconNode.Tests
{
    public static class TestData
    {
        private const byte BLS_WITHDRAWAL_PREFIX = 0x00;

        public static void GetMinimalConfiguration(
            out ChainConstants chainConstants,
            out MiscellaneousParameters miscellaneousParameters,
            out GweiValues gweiValues,
            out InitialValues initalValues,
            out TimeParameters timeParameters,
            out StateListLengths stateListLengths,
            out MaxOperationsPerBlock maxOperationsPerBlock)
        {
            chainConstants = new ChainConstants();
            miscellaneousParameters = new MiscellaneousParameters()
            {
                MinimumGenesisActiveValidatorCount = 64,
                MinimumGenesisTime = 1578009600 // Jan 3, 2020
            };
            gweiValues = new GweiValues()
            {
                MaximumEffectiveBalance = new Gwei(((ulong)1 << 5) * 1000 * 1000 * 1000),
                EffectiveBalanceIncrement = new Gwei(1000 * 1000 * 1000)
            };
            initalValues = new InitialValues()
            {
                GenesisEpoch = new Epoch(0),
                BlsWithdrawalPrefix = 0x00
            };
            timeParameters = new TimeParameters()
            {
                SlotsPerEpoch = 8
            };
            stateListLengths = new StateListLengths()
            {
                ValidatorRegistryLimit = (ulong)1 << 40
            };
            maxOperationsPerBlock = new MaxOperationsPerBlock()
            {
                MaximumDeposits = 16
            };
        }

        public static IEnumerable<byte[]> PrivateKeys(TimeParameters timeParameters)
        {
            // Private key is ~255 bits (32 bytes) long
            var privateKeys = Enumerable.Range(0, (int)timeParameters.SlotsPerEpoch * 16).Select(x => {
                var key = new byte[32];
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

        public static (IEnumerable<Deposit>, Hash32) PrepareGenesisDeposits(ChainConstants chainConstants,
            TimeParameters timeParameters, BeaconChainUtility beaconChainUtility, int genesisValidatorCount, Gwei amount,
            bool signed)
        {
            var privateKeys = PrivateKeys(timeParameters).ToArray();
            BlsPublicKey[] publicKeys;
            if (signed)
            {
                publicKeys = PublicKeys(privateKeys).ToArray();
            } 
            else
            {
                publicKeys = privateKeys.Select(x => new BlsPublicKey(x)).ToArray();
            }
            var depositDataList = new List<DepositData>();
            var genesisDeposits = new List<Deposit>();
            Hash32 root = new Hash32();
            for (var validatorIndex = 0; validatorIndex < genesisValidatorCount; validatorIndex++)
            {
                var publicKey = publicKeys[validatorIndex];
                var privateKey = privateKeys[validatorIndex];
                // insecurely use pubkey as withdrawal key if no credentials provided
                var withdrawalCredentialBytes = TestUtility.Hash(publicKey.AsSpan());
                withdrawalCredentialBytes[0] = BLS_WITHDRAWAL_PREFIX;
                var withdrawalCredentials = new Hash32(withdrawalCredentialBytes);
                (var deposit, var depositRoot) = BuildDeposit(chainConstants, beaconChainUtility, null, depositDataList, publicKey, privateKey, amount, withdrawalCredentials, signed);
                root = depositRoot;
                genesisDeposits.Add(deposit);
            }
            return (genesisDeposits, root);
        }

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

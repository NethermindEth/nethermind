using System;
using System.Collections.Generic;
using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Ssz;
using Cortex.Containers;
using Cortex.Cryptography;
using Microsoft.Extensions.Options;

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

        public static Validator BuildMockValidator(ChainConstants chainConstants, InitialValues initialValues, GweiValues gweiValues, TimeParameters timeParameters, ulong validatorIndex, Gwei balance)
        {
            var privateKeys = PrivateKeys(timeParameters);
            var publicKeys = PublicKeys(privateKeys).ToArray();
            var publicKey = publicKeys[validatorIndex];
            // insecurely use pubkey as withdrawal key if no credentials provided
            var withdrawalCredentialBytes = TestUtility.Hash(publicKey.AsSpan());
            withdrawalCredentialBytes[0] = initialValues.BlsWithdrawalPrefix;
            var withdrawalCredentials = new Hash32(withdrawalCredentialBytes);

            var validator = new Validator(
                publicKey,
                withdrawalCredentials,
                chainConstants.FarFutureEpoch,
                chainConstants.FarFutureEpoch,
                chainConstants.FarFutureEpoch,
                chainConstants.FarFutureEpoch,
                Gwei.Min(balance - balance % gweiValues.EffectiveBalanceIncrement, gweiValues.MaximumEffectiveBalance)
                );

            return validator;
        }

        public static BeaconState CreateGenesisState(ChainConstants chainConstants, InitialValues initialValues, GweiValues gweiValues, TimeParameters timeParameters, StateListLengths stateListLengths, MaxOperationsPerBlock maxOperationsPerBlock, ulong numberOfValidators)
        {
            var depositRoot = new Hash32(Enumerable.Repeat((byte)0x42, 32).ToArray());
            var state = new BeaconState(
                0,
                numberOfValidators,
                new Eth1Data(depositRoot, numberOfValidators),
                new BeaconBlockHeader((new BeaconBlockBody()).HashTreeRoot(maxOperationsPerBlock)),
                timeParameters.SlotsPerHistoricalRoot,
                stateListLengths.EpochsPerHistoricalVector
                );

            // We directly insert in the initial validators,
            // as it is much faster than creating and processing genesis deposits for every single test case.
            for (var index = (ulong)0; index < numberOfValidators; index++)
            {
                var validator = BuildMockValidator(chainConstants, initialValues, gweiValues, timeParameters, index, gweiValues.MaximumEffectiveBalance);
                state.AddValidatorWithBalance(validator, gweiValues.MaximumEffectiveBalance);
            }

            // Process genesis activations
            foreach (var validator in state.Validators)
            {
                if (validator.EffectiveBalance >= gweiValues.MaximumEffectiveBalance)
                {
                    validator.SetEligible(initialValues.GenesisEpoch);
                    validator.SetActive(initialValues.GenesisEpoch);
                }
            }

            return state;
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

        public static void GetMinimalConfiguration(
            out ChainConstants chainConstants,
            out IOptionsMonitor<MiscellaneousParameters> miscellaneousParameterOptions,
            out IOptionsMonitor<GweiValues> gweiValueOptions,
            out IOptionsMonitor<InitialValues> initialValueOptions,
            out IOptionsMonitor<TimeParameters> timeParameterOptions,
            out IOptionsMonitor<StateListLengths> stateListLengthOptions,
            out IOptionsMonitor<MaxOperationsPerBlock> maxOperationsPerBlockOptions)
        {
            chainConstants = new ChainConstants();
            miscellaneousParameterOptions = TestOptionsMonitor.Create(new MiscellaneousParameters()
            {
                ShardCount = new Shard(8),
                TargetCommitteeSize = 4,
                ShuffleRoundCount = 10,
                MinimumGenesisActiveValidatorCount = 64,
                MinimumGenesisTime = 1578009600 // Jan 3, 2020
            });
            gweiValueOptions = TestOptionsMonitor.Create(new GweiValues()
            {
                MaximumEffectiveBalance = new Gwei(((ulong)1 << 5) * 1000 * 1000 * 1000),
                EffectiveBalanceIncrement = new Gwei(1000 * 1000 * 1000)
            });
            initialValueOptions = TestOptionsMonitor.Create(new InitialValues()
            {
                GenesisEpoch = new Epoch(0),
                BlsWithdrawalPrefix = 0x00
            });
            timeParameterOptions = TestOptionsMonitor.Create(new TimeParameters()
            {
                SlotsPerEpoch = new Slot(8),
                MinimumSeedLookahead = new Epoch(1),
                SlotsPerHistoricalRoot = new Slot(64)
            });
            stateListLengthOptions = TestOptionsMonitor.Create(new StateListLengths()
            {
                EpochsPerHistoricalVector = new Epoch(64),
                ValidatorRegistryLimit = (ulong)1 << 40
            });
            maxOperationsPerBlockOptions = TestOptionsMonitor.Create(new MaxOperationsPerBlock()
            {
                MaximumDeposits = 16
            });
        }

        public static (IEnumerable<Deposit>, Hash32) PrepareGenesisDeposits(ChainConstants chainConstants, InitialValues initialValues,
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
                withdrawalCredentialBytes[0] = initialValues.BlsWithdrawalPrefix;
                var withdrawalCredentials = new Hash32(withdrawalCredentialBytes);
                (var deposit, var depositRoot) = BuildDeposit(chainConstants, beaconChainUtility, null, depositDataList, publicKey, privateKey, amount, withdrawalCredentials, signed);
                root = depositRoot;
                genesisDeposits.Add(deposit);
            }
            return (genesisDeposits, root);
        }

        public static IEnumerable<byte[]> PrivateKeys(TimeParameters timeParameters)
        {
            // Private key is ~255 bits (32 bytes) long
            var privateKeys = Enumerable.Range(0, (int)(ulong)timeParameters.SlotsPerEpoch * 16).Select(x =>
            {
                var key = new byte[32];
                var bytes = BitConverter.GetBytes((ulong)(x + 1));
                bytes.CopyTo(key, 0);
                return key;
            });
            return privateKeys;
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
    }
}

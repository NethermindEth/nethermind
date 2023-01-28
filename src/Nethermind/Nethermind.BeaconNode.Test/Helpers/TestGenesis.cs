// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nethermind.Core2.Configuration;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
namespace Nethermind.BeaconNode.Test.Helpers
{
    public static class TestGenesis
    {
        public static Validator BuildMockValidator(ChainConstants chainConstants, InitialValues initialValues, GweiValues gweiValues, TimeParameters timeParameters, ulong validatorIndex, Gwei balance)
        {
            var publicKeys = TestKeys.PublicKeys(timeParameters).ToArray();
            var publicKey = publicKeys[validatorIndex];
            // insecurely use pubkey as withdrawal key if no credentials provided
            var withdrawalCredentialBytes = TestSecurity.Hash(publicKey.AsSpan());
            withdrawalCredentialBytes[0] = initialValues.BlsWithdrawalPrefix;
            var withdrawalCredentials = new Bytes32(withdrawalCredentialBytes);

            var validator = new Validator(
                publicKey,
                withdrawalCredentials,
                Gwei.Min(balance - balance % gweiValues.EffectiveBalanceIncrement, gweiValues.MaximumEffectiveBalance),
                false,
                chainConstants.FarFutureEpoch,
                chainConstants.FarFutureEpoch,
                chainConstants.FarFutureEpoch,
                chainConstants.FarFutureEpoch);

            return validator;
        }

        public static BeaconState CreateGenesisState(IServiceProvider testServiceProvider, ulong numberOfValidators)
        {
            ChainConstants chainConstants = testServiceProvider.GetService<ChainConstants>();
            GweiValues gweiValues = testServiceProvider.GetService<IOptions<GweiValues>>().Value;
            InitialValues initialValues = testServiceProvider.GetService<IOptions<InitialValues>>().Value;
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            StateListLengths stateListLengths = testServiceProvider.GetService<IOptions<StateListLengths>>().Value;
            ICryptographyService cryptographyService = testServiceProvider.GetService<ICryptographyService>();

            var eth1BlockHash = new Bytes32(Enumerable.Repeat((byte)0x42, 32).ToArray());
            var state = new BeaconState(
                0,
                new Core2.Containers.Fork(new ForkVersion(new byte[ForkVersion.Length]), new ForkVersion(new byte[ForkVersion.Length]), Epoch.Zero),
                new Eth1Data(Root.Zero, numberOfValidators, eth1BlockHash),
                //numberOfValidators,
                new BeaconBlockHeader(cryptographyService.HashTreeRoot(BeaconBlockBody.Zero)),
                Enumerable.Repeat(eth1BlockHash, (int)stateListLengths.EpochsPerHistoricalVector).ToArray(),
                timeParameters.SlotsPerHistoricalRoot,
                stateListLengths.EpochsPerHistoricalVector,
                stateListLengths.EpochsPerSlashingsVector,
                chainConstants.JustificationBitsLength
                );

            // We directly insert in the initial validators,
            // as it is much faster than creating and processing genesis deposits for every single test case.
            for (var index = (ulong)0; index < numberOfValidators; index++)
            {
                var validator = BuildMockValidator(chainConstants, initialValues, gweiValues, timeParameters, index, gweiValues.MaximumEffectiveBalance);
                state.AddValidatorWithBalance(validator, gweiValues.MaximumEffectiveBalance);
                state.IncreaseEth1DepositIndex();
            }

            // Process genesis activations
            foreach (var validator in state.Validators)
            {
                if (validator.EffectiveBalance >= gweiValues.MaximumEffectiveBalance)
                {
                    validator.SetEligible(chainConstants.GenesisEpoch);
                    validator.SetActive(chainConstants.GenesisEpoch);
                }
            }

            return state;
        }
    }
}

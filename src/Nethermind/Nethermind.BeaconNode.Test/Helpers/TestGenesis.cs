using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Ssz;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Tests.Helpers
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
            var withdrawalCredentials = new Hash32(withdrawalCredentialBytes);

            var validator = new Validator(
                publicKey,
                withdrawalCredentials,
                Gwei.Min(balance - balance % gweiValues.EffectiveBalanceIncrement, gweiValues.MaximumEffectiveBalance)
,
                chainConstants.FarFutureEpoch,
                chainConstants.FarFutureEpoch,
                chainConstants.FarFutureEpoch,
                chainConstants.FarFutureEpoch);

            return validator;
        }

        public static BeaconState CreateGenesisState(IServiceProvider testServiceProvider, ulong numberOfValidators)
        {
            var chainConstants = testServiceProvider.GetService<ChainConstants>();
            var miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            var gweiValues = testServiceProvider.GetService<IOptions<GweiValues>>().Value;
            var initialValues = testServiceProvider.GetService<IOptions<InitialValues>>().Value;
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            var stateListLengths = testServiceProvider.GetService<IOptions<StateListLengths>>().Value;
            var maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;

            var depositRoot = new Hash32(Enumerable.Repeat((byte)0x42, 32).ToArray());
            var state = new BeaconState(
                0,
                numberOfValidators,
                new Eth1Data(numberOfValidators, depositRoot),
                new BeaconBlockHeader((new BeaconBlockBody()).HashTreeRoot(miscellaneousParameters, maxOperationsPerBlock)),
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
    }
}

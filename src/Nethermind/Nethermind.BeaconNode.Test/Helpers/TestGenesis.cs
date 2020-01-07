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
            var withdrawalCredentials = new Hash32(withdrawalCredentialBytes);

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
            var chainConstants = testServiceProvider.GetService<ChainConstants>();
            var miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            var gweiValues = testServiceProvider.GetService<IOptions<GweiValues>>().Value;
            var initialValues = testServiceProvider.GetService<IOptions<InitialValues>>().Value;
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            var stateListLengths = testServiceProvider.GetService<IOptions<StateListLengths>>().Value;
            var maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;
            ICryptographyService cryptographyService = testServiceProvider.GetService<ICryptographyService>();

            var depositRoot = new Hash32(Enumerable.Repeat((byte)0x42, 32).ToArray());
            var state = new BeaconState(
                0,
                numberOfValidators,
                new Eth1Data(numberOfValidators, depositRoot),
                new BeaconBlockHeader(cryptographyService.HashTreeRoot(new BeaconBlockBody())),
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

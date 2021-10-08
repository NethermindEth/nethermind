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

using System.Text.Json;
using Nethermind.Core2.Api;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;

namespace Nethermind.Core2.Json
{
    public static class JsonSerializerOptionsExtensions
    {
        public static void ConfigureNethermindCore2(this JsonSerializerOptions options)
        {
            options.PropertyNamingPolicy = new JsonNamingPolicySnakeCase();
            
            options.Converters.Add(new JsonConverterByteArrayPrefixedHex());

            options.Converters.Add(new JsonConverterBlsPublicKey());
            options.Converters.Add(new JsonConverterBlsSignature());
            options.Converters.Add(new JsonConverterBytes32());
            options.Converters.Add(new JsonConverterCommitteeIndex());
            options.Converters.Add(new JsonConverterDomain());
            options.Converters.Add(new JsonConverterEpoch());
            options.Converters.Add(new JsonConverterForkVersion());
            options.Converters.Add(new JsonConverterGwei());
            options.Converters.Add(new JsonConverterRoot());
            options.Converters.Add(new JsonConverterShard());
            options.Converters.Add(new JsonConverterSlot());
            options.Converters.Add(new JsonConverterValidatorIndex());
            options.Converters.Add(new JsonConverterBitArray());
            
            // Constructor converters
            options.Converters.Add(new LastConstructorJsonConverter<SyncingStatus>());
            options.Converters.Add(new LastConstructorJsonConverter<Syncing>());
            options.Converters.Add(new LastConstructorJsonConverter<ForkInformation>());
            options.Converters.Add(new LastConstructorJsonConverter<Fork>());
            options.Converters.Add(new LastConstructorJsonConverter<ValidatorDuty>());
            options.Converters.Add(new LastConstructorJsonConverter<BeaconBlock>());
            options.Converters.Add(new LastConstructorJsonConverter<BeaconBlockBody>());
            options.Converters.Add(new LastConstructorJsonConverter<Eth1Data>());
            options.Converters.Add(new LastConstructorJsonConverter<Deposit>());
            options.Converters.Add(new LastConstructorJsonConverter<DepositData>());
            options.Converters.Add(new LastConstructorJsonConverter<SignedBeaconBlock>());
            options.Converters.Add(new LastConstructorJsonConverter<Attestation>());
            options.Converters.Add(new LastConstructorJsonConverter<AttestationData>());
            options.Converters.Add(new LastConstructorJsonConverter<Checkpoint>());
        }
    }
}

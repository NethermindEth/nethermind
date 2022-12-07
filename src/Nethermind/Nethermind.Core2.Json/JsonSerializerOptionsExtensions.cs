// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

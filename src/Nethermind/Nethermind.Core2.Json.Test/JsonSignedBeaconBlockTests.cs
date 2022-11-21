// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using NUnit.Framework;
using Shouldly;

namespace Nethermind.Core2.Json.Test
{
    [TestFixture]
    public class JsonSignedBeaconBlockTests
    {
        [Test]
        public async Task SignedBeaconBlock_RoundTripEmpty()
        {
            // Arrange
            JsonSerializerOptions options = new JsonSerializerOptions();
            options.ConfigureNethermindCore2();

            Eth1Data eth1Data = new Eth1Data(
                new Root(Enumerable.Repeat((byte)0x12, 32).ToArray()),
                64,
                new Bytes32(Enumerable.Repeat((byte)0x34, 32).ToArray()));

            BlsSignature randaoReveal = new BlsSignature(Enumerable.Repeat((byte)0xfe, 96).ToArray());

            BeaconBlockBody beaconBlockBody = new BeaconBlockBody(
                randaoReveal,
                eth1Data,
                new Bytes32(new byte[32]),
                new ProposerSlashing[0],
                new AttesterSlashing[0],
                new Attestation[0],
                new Deposit[0],
                new SignedVoluntaryExit[0]
            );

            BeaconBlock beaconBlock = new BeaconBlock(
                new Slot(1),
                new Root(Enumerable.Repeat((byte)0x78, 32).ToArray()),
                new Root(Enumerable.Repeat((byte)0x9a, 32).ToArray()),
                beaconBlockBody);

            SignedBeaconBlock signedBeaconBlock = new SignedBeaconBlock(
                beaconBlock,
                new BlsSignature(Enumerable.Repeat((byte)0x0e, 96).ToArray())
            );

            // Act - round trip to string
            await using MemoryStream outputMemoryStream = new MemoryStream();
            await JsonSerializer.SerializeAsync(outputMemoryStream, signedBeaconBlock, options);
            string jsonString = Encoding.UTF8.GetString(outputMemoryStream.ToArray());

            Console.WriteLine(jsonString);

            // Assert - Round trip

            await using MemoryStream inputMemoryStream = new MemoryStream(Encoding.UTF8.GetBytes(jsonString));
            SignedBeaconBlock roundTripSignedBeaconBlock = await JsonSerializer.DeserializeAsync<SignedBeaconBlock>(inputMemoryStream, options);

            roundTripSignedBeaconBlock.Message.Body.Eth1Data.BlockHash.AsSpan()[1].ShouldBe((byte)0x34);
        }

        [Test]
        public async Task SignedBeaconBlock_RoundTripWithDeposit()
        {
            // Arrange
            JsonSerializerOptions options = new JsonSerializerOptions();
            options.ConfigureNethermindCore2();

            Eth1Data eth1Data = new Eth1Data(
                new Root(Enumerable.Repeat((byte)0x12, 32).ToArray()),
                64,
                new Bytes32(Enumerable.Repeat((byte)0x34, 32).ToArray()));

            int depositContractTreeDepth = 32;
            Deposit deposit = new Deposit(
                Enumerable.Repeat(new Bytes32(Enumerable.Repeat((byte)0x11, 32).ToArray()), depositContractTreeDepth + 1),
                new Ref<DepositData>(new DepositData(
                    new BlsPublicKey(Enumerable.Repeat((byte)0x22, 48).ToArray()),
                    new Bytes32(Enumerable.Repeat((byte)0x33, 32).ToArray()),
                    new Gwei(32_000_000),
                    new BlsSignature(Enumerable.Repeat((byte)0x44, 96).ToArray())
                    )));

            BlsSignature randaoReveal = new BlsSignature(Enumerable.Repeat((byte)0xfe, 96).ToArray());

            BeaconBlockBody beaconBlockBody = new BeaconBlockBody(
                randaoReveal,
                eth1Data,
                new Bytes32(new byte[32]),
                new ProposerSlashing[0],
                new AttesterSlashing[0],
                new Attestation[0],
                new Deposit[] { deposit },
                new SignedVoluntaryExit[0]
            );

            BeaconBlock beaconBlock = new BeaconBlock(
                new Slot(1),
                new Root(Enumerable.Repeat((byte)0x78, 32).ToArray()),
                new Root(Enumerable.Repeat((byte)0x9a, 32).ToArray()),
                beaconBlockBody);

            SignedBeaconBlock signedBeaconBlock = new SignedBeaconBlock(
                beaconBlock,
                new BlsSignature(Enumerable.Repeat((byte)0x0e, 96).ToArray())
            );

            // Act - round trip to string
            await using MemoryStream outputMemoryStream = new MemoryStream();
            await JsonSerializer.SerializeAsync(outputMemoryStream, signedBeaconBlock, options);
            string jsonString = Encoding.UTF8.GetString(outputMemoryStream.ToArray());

            Console.WriteLine(jsonString);

            // Assert - Round trip

            await using MemoryStream inputMemoryStream = new MemoryStream(Encoding.UTF8.GetBytes(jsonString));
            SignedBeaconBlock roundTripSignedBeaconBlock = await JsonSerializer.DeserializeAsync<SignedBeaconBlock>(inputMemoryStream, options);

            roundTripSignedBeaconBlock.Message.Body.Eth1Data.BlockHash.AsSpan()[31].ShouldBe((byte)0x34);
            roundTripSignedBeaconBlock.Message.Body.Deposits[0].Data.Item.Signature.AsSpan()[95].ShouldBe((byte)0x44);
            roundTripSignedBeaconBlock.Message.Body.Deposits[0].Proof[32].AsSpan()[31].ShouldBe((byte)0x11);
        }
    }
}

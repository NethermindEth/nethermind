// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Optimism.CL;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.CL;

public class SystemConfigDeriverTests
{
    private static readonly Address L1SystemConfigAddress = new("0x229047fed2591dbec1eF1118d64F7aF3dB9EB290");

    private static readonly AbiSignature AddressSignature = new("address", AbiType.Address);
    private static readonly AbiSignature BytesSignature = new("bytes", AbiType.DynamicBytes);

    [Test]
    public void UpdateSystemConfigFromL1BLock_UpdatedBatcher()
    {
        var rawAddress = new byte[Address.Size];
        rawAddress[19] = 0xAA;
        var address = new Address(rawAddress);

        var encodedAddress = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, AddressSignature, address);
        var encodedData = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, BytesSignature, encodedAddress);

        var blockHeader = Build.A.BlockHeader
            .WithHash(TestItem.KeccakA)
            .TestObject;

        var receiptFinder = Substitute.For<IReceiptFinder>();
        receiptFinder.Get(TestItem.KeccakA).Returns([
            new TxReceipt
            {
                StatusCode = StatusCode.Success,
                Logs =
                [
                    Build.A.LogEntry
                        .WithAddress(L1SystemConfigAddress)
                        .WithData(encodedData)
                        .WithTopics(
                            SystemConfigUpdate.EventABIHash,
                            SystemConfigUpdate.EventVersion0,
                            SystemConfigUpdate.Batcher)
                        .TestObject
                ]
            }
        ]);

        var deriver = new SystemConfigDeriver(
            new RollupConfig { L1SystemConfigAddress = L1SystemConfigAddress },
            receiptFinder,
            Substitute.For<IOptimismSpecHelper>()
        );
        var actualConfig = deriver.UpdateSystemConfigFromL1BLock(new SystemConfig(), blockHeader);
        var expectedConfig = new SystemConfig { BatcherAddress = address };

        actualConfig.Should().Be(expectedConfig);
    }
}

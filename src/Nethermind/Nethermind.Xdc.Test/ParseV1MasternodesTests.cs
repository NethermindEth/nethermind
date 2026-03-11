// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using NUnit.Framework;
using System;

namespace Nethermind.Xdc.Test;

internal class ParseV1MasternodesTests
{
    private static byte[] BuildExtraData(int vanity, int addressCount, int seal, int extraTrailingBytes = 0)
    {
        int totalLength = vanity + addressCount * Address.Size + seal + extraTrailingBytes;
        byte[] data = new byte[totalLength];
        for (int i = 0; i < addressCount; i++)
        {
            // Fill each address slot with a recognizable pattern
            data[vanity + i * Address.Size] = (byte)(i + 1);
        }
        return data;
    }

    [Test]
    public void TooShort_ThrowsArgumentException()
    {
        byte[] extraData = new byte[XdcConstants.ExtraVanity + XdcConstants.ExtraSeal - 1];

        Assert.Throws<ArgumentException>(() => extraData.ParseV1Masternodes());
    }

    [Test]
    public void ExactlyMinLength_NoAddresses_ThrowsArgumentException()
    {
        byte[] extraData = new byte[XdcConstants.ExtraVanity + XdcConstants.ExtraSeal];

        Assert.Throws<ArgumentException>(() => extraData.ParseV1Masternodes());
    }

    [Test]
    public void NotDivisibleByAddressSize_ThrowsArgumentException()
    {
        // Add some bytes that aren't a multiple of Address.Size (20)
        byte[] extraData = new byte[XdcConstants.ExtraVanity + XdcConstants.ExtraSeal + Address.Size + 1];

        Assert.Throws<ArgumentException>(() => extraData.ParseV1Masternodes());
    }

    [Test]
    public void ValidSingleAddress_ReturnsOneAddress()
    {
        byte[] extraData = BuildExtraData(XdcConstants.ExtraVanity, 1, XdcConstants.ExtraSeal);

        Address[] result = extraData.ParseV1Masternodes();

        Assert.That(result, Has.Length.EqualTo(1));
    }

    [Test]
    public void ValidMultipleAddresses_ReturnsCorrectCount()
    {
        int addressCount = 3;
        byte[] extraData = BuildExtraData(XdcConstants.ExtraVanity, addressCount, XdcConstants.ExtraSeal);

        Address[] result = extraData.ParseV1Masternodes();

        Assert.That(result, Has.Length.EqualTo(addressCount));
    }

    [Test]
    public void AddressesAreCorrectlyExtracted()
    {
        byte[] extraData = new byte[XdcConstants.ExtraVanity + Address.Size + XdcConstants.ExtraSeal];
        // Write a recognizable address
        for (int j = 0; j < Address.Size; j++)
            extraData[XdcConstants.ExtraVanity + j] = (byte)(j + 1);

        Address[] result = extraData.ParseV1Masternodes();

        Assert.That(result, Has.Length.EqualTo(1));
        for (int j = 0; j < Address.Size; j++)
            Assert.That(result[0].Bytes[j], Is.EqualTo((byte)(j + 1)));
    }
}

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;
using Nethermind.Network.Contract.P2P;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P;

[Parallelizable(ParallelScope.Self)]
public class ProtocolParserTests
{
    [TestCase("eth", Protocol.Eth)]
    [TestCase("p2p", Protocol.P2P)]
    [TestCase("shh", Protocol.Shh)]
    [TestCase("bzz", Protocol.Bzz)]
    [TestCase("par", Protocol.Par)]
    [TestCase("snap", Protocol.Snap)]
    [TestCase("nodedata", Protocol.NodeData)]
    [TestCase("aa", Protocol.AA)]
    public void TryGetProtocolCode_KnownProtocol_ReturnsTrue(string input, string expected)
    {
        byte[] protocolBytes = Encoding.UTF8.GetBytes(input);
        bool result = ProtocolParser.TryGetProtocolCode(protocolBytes, out string? protocol);

        Assert.That(result, Is.True);
        Assert.That(protocol, Is.EqualTo(expected));
    }

    [TestCase("", Description = "Empty")]
    [TestCase("e", Description = "Single char")]
    [TestCase("xyz", Description = "Unknown 3-char")]
    [TestCase("ethx", Description = "Partial match eth + extra char")]
    [TestCase("eth69", Description = "Wrong length 5 chars")]
    [TestCase("sna", Description = "Partial match snap truncated")]
    [TestCase("abcdef", Description = "Length 6")]
    [TestCase("abcdefg", Description = "Length 7")]
    [TestCase("abcdefghi", Description = "Length 9")]
    [TestCase("ETH", Description = "Upper case")]
    [TestCase("Eth", Description = "Mixed case")]
    public void TryGetProtocolCode_InvalidInput_ReturnsFalse(string input)
    {
        byte[] protocolBytes = Encoding.UTF8.GetBytes(input);
        bool result = ProtocolParser.TryGetProtocolCode(protocolBytes, out string? protocol);

        Assert.That(result, Is.False);
        Assert.That(protocol, Is.Null);
    }

    [Test]
    public void TryGetProtocolCode_EmptySpan_ReturnsFalse()
    {
        ReadOnlySpan<byte> emptySpan = ReadOnlySpan<byte>.Empty;
        bool result = ProtocolParser.TryGetProtocolCode(emptySpan, out string? protocol);

        Assert.That(result, Is.False);
        Assert.That(protocol, Is.Null);
    }

    [TestCase("eth", Protocol.Eth)]
    [TestCase("snap", Protocol.Snap)]
    [TestCase("nodedata", Protocol.NodeData)]
    public void TryGetProtocolCode_WithSpan_ReturnsTrue(string input, string expected)
    {
        ReadOnlySpan<byte> protocolSpan = Encoding.UTF8.GetBytes(input);
        bool result = ProtocolParser.TryGetProtocolCode(protocolSpan, out string? protocol);

        Assert.That(result, Is.True);
        Assert.That(protocol, Is.EqualTo(expected));
    }

    [TestCase("eth", 0x687465u)]
    [TestCase("p2p", 0x703270u)]
    [TestCase("shh", 0x686873u)]
    [TestCase("bzz", 0x7A7A62u)]
    [TestCase("par", 0x726170u)]
    public void TryGetProtocolCode_ValidatesThreeByteHexConstants(string input, uint expected)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(input);
        uint value = (uint)bytes[0] | ((uint)bytes[1] << 8) | ((uint)bytes[2] << 16);

        Assert.That(value, Is.EqualTo(expected), $"Hex constant for '{input}' should match");
    }

    [Test]
    public void TryGetProtocolCode_ValidatesHexConstants_Snap()
    {
        byte[] snapBytes = Encoding.UTF8.GetBytes("snap");
        uint snapValue = BitConverter.ToUInt32(snapBytes, 0);

        Assert.That(snapValue, Is.EqualTo(0x70616E73u), "Hex constant for 'snap' should match");
    }

    [Test]
    public void TryGetProtocolCode_ValidatesHexConstants_NodeData()
    {
        byte[] nodedataBytes = Encoding.UTF8.GetBytes("nodedata");
        ulong nodedataValue = BitConverter.ToUInt64(nodedataBytes, 0);

        Assert.That(nodedataValue, Is.EqualTo(0x6174616465646F6Eul), "Hex constant for 'nodedata' should match");
    }

    [Test]
    public void TryGetProtocolCode_ValidatesHexConstants_AA()
    {
        byte[] aaBytes = Encoding.UTF8.GetBytes("aa");
        ushort aaValue = BitConverter.ToUInt16(aaBytes, 0);

        Assert.That(aaValue, Is.EqualTo(0x6161), "Hex constant for 'aa' should match");
    }
}

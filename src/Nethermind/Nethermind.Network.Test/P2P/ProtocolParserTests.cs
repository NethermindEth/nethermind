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
    [Test]
    public void TryGetProtocolCode_Eth_ReturnsTrue()
    {
        byte[] protocolBytes = Encoding.UTF8.GetBytes("eth");
        bool result = ProtocolParser.TryGetProtocolCode(protocolBytes, out string? protocol);

        Assert.That(result, Is.True);
        Assert.That(protocol, Is.EqualTo(Protocol.Eth));
    }

    [Test]
    public void TryGetProtocolCode_P2p_ReturnsTrue()
    {
        byte[] protocolBytes = Encoding.UTF8.GetBytes("p2p");
        bool result = ProtocolParser.TryGetProtocolCode(protocolBytes, out string? protocol);

        Assert.That(result, Is.True);
        Assert.That(protocol, Is.EqualTo(Protocol.P2P));
    }

    [Test]
    public void TryGetProtocolCode_Shh_ReturnsTrue()
    {
        byte[] protocolBytes = Encoding.UTF8.GetBytes("shh");
        bool result = ProtocolParser.TryGetProtocolCode(protocolBytes, out string? protocol);

        Assert.That(result, Is.True);
        Assert.That(protocol, Is.EqualTo(Protocol.Shh));
    }

    [Test]
    public void TryGetProtocolCode_Bzz_ReturnsTrue()
    {
        byte[] protocolBytes = Encoding.UTF8.GetBytes("bzz");
        bool result = ProtocolParser.TryGetProtocolCode(protocolBytes, out string? protocol);

        Assert.That(result, Is.True);
        Assert.That(protocol, Is.EqualTo(Protocol.Bzz));
    }

    [Test]
    public void TryGetProtocolCode_Par_ReturnsTrue()
    {
        byte[] protocolBytes = Encoding.UTF8.GetBytes("par");
        bool result = ProtocolParser.TryGetProtocolCode(protocolBytes, out string? protocol);

        Assert.That(result, Is.True);
        Assert.That(protocol, Is.EqualTo(Protocol.Par));
    }

    [Test]
    public void TryGetProtocolCode_Ndm_ReturnsTrue()
    {
        byte[] protocolBytes = Encoding.UTF8.GetBytes("ndm");
        bool result = ProtocolParser.TryGetProtocolCode(protocolBytes, out string? protocol);

        Assert.That(result, Is.True);
        Assert.That(protocol, Is.EqualTo(Protocol.Ndm));
    }

    [Test]
    public void TryGetProtocolCode_Snap_ReturnsTrue()
    {
        byte[] protocolBytes = Encoding.UTF8.GetBytes("snap");
        bool result = ProtocolParser.TryGetProtocolCode(protocolBytes, out string? protocol);

        Assert.That(result, Is.True);
        Assert.That(protocol, Is.EqualTo(Protocol.Snap));
    }

    [Test]
    public void TryGetProtocolCode_NodeData_ReturnsTrue()
    {
        byte[] protocolBytes = Encoding.UTF8.GetBytes("nodedata");
        bool result = ProtocolParser.TryGetProtocolCode(protocolBytes, out string? protocol);

        Assert.That(result, Is.True);
        Assert.That(protocol, Is.EqualTo(Protocol.NodeData));
    }

    [Test]
    public void TryGetProtocolCode_AA_ReturnsTrue()
    {
        byte[] protocolBytes = Encoding.UTF8.GetBytes("aa");
        bool result = ProtocolParser.TryGetProtocolCode(protocolBytes, out string? protocol);

        Assert.That(result, Is.True);
        Assert.That(protocol, Is.EqualTo(Protocol.AA));
    }

    [Test]
    public void TryGetProtocolCode_EmptySpan_ReturnsFalse()
    {
        ReadOnlySpan<byte> emptySpan = ReadOnlySpan<byte>.Empty;
        bool result = ProtocolParser.TryGetProtocolCode(emptySpan, out string? protocol);

        Assert.That(result, Is.False);
        Assert.That(protocol, Is.Null);
    }

    [Test]
    public void TryGetProtocolCode_UnknownProtocol_ReturnsFalse()
    {
        byte[] protocolBytes = Encoding.UTF8.GetBytes("xyz");
        bool result = ProtocolParser.TryGetProtocolCode(protocolBytes, out string? protocol);

        Assert.That(result, Is.False);
        Assert.That(protocol, Is.Null);
    }

    [Test]
    public void TryGetProtocolCode_IncorrectLength_ReturnsFalse()
    {
        byte[] protocolBytes = Encoding.UTF8.GetBytes("e");
        bool result = ProtocolParser.TryGetProtocolCode(protocolBytes, out string? protocol);

        Assert.That(result, Is.False);
        Assert.That(protocol, Is.Null);
    }

    [Test]
    public void TryGetProtocolCode_WrongLength_FiveChars_ReturnsFalse()
    {
        byte[] protocolBytes = Encoding.UTF8.GetBytes("ethxx");
        bool result = ProtocolParser.TryGetProtocolCode(protocolBytes, out string? protocol);

        Assert.That(result, Is.False);
        Assert.That(protocol, Is.Null);
    }

    [Test]
    public void TryGetProtocolCode_UpperCase_ReturnsFalse()
    {
        byte[] protocolBytes = Encoding.UTF8.GetBytes("ETH");
        bool result = ProtocolParser.TryGetProtocolCode(protocolBytes, out string? protocol);

        Assert.That(result, Is.False);
        Assert.That(protocol, Is.Null);
    }

    [Test]
    public void TryGetProtocolCode_MixedCase_ReturnsFalse()
    {
        byte[] protocolBytes = Encoding.UTF8.GetBytes("Eth");
        bool result = ProtocolParser.TryGetProtocolCode(protocolBytes, out string? protocol);

        Assert.That(result, Is.False);
        Assert.That(protocol, Is.Null);
    }

    [Test]
    public void TryGetProtocolCode_ValidatesHexConstants_Eth()
    {
        byte[] ethBytes = Encoding.UTF8.GetBytes("eth");
        uint ethValue = (uint)ethBytes[0] | ((uint)ethBytes[1] << 8) | ((uint)ethBytes[2] << 16);

        Assert.That(ethValue, Is.EqualTo(0x687465u), "Hex constant for 'eth' should match");
    }

    [Test]
    public void TryGetProtocolCode_ValidatesHexConstants_P2p()
    {
        byte[] p2pBytes = Encoding.UTF8.GetBytes("p2p");
        uint p2pValue = (uint)p2pBytes[0] | ((uint)p2pBytes[1] << 8) | ((uint)p2pBytes[2] << 16);

        Assert.That(p2pValue, Is.EqualTo(0x703270u), "Hex constant for 'p2p' should match");
    }

    [Test]
    public void TryGetProtocolCode_ValidatesHexConstants_Shh()
    {
        byte[] shhBytes = Encoding.UTF8.GetBytes("shh");
        uint shhValue = (uint)shhBytes[0] | ((uint)shhBytes[1] << 8) | ((uint)shhBytes[2] << 16);

        Assert.That(shhValue, Is.EqualTo(0x686873u), "Hex constant for 'shh' should match");
    }

    [Test]
    public void TryGetProtocolCode_ValidatesHexConstants_Bzz()
    {
        byte[] bzzBytes = Encoding.UTF8.GetBytes("bzz");
        uint bzzValue = (uint)bzzBytes[0] | ((uint)bzzBytes[1] << 8) | ((uint)bzzBytes[2] << 16);

        Assert.That(bzzValue, Is.EqualTo(0x7A7A62u), "Hex constant for 'bzz' should match");
    }

    [Test]
    public void TryGetProtocolCode_ValidatesHexConstants_Par()
    {
        byte[] parBytes = Encoding.UTF8.GetBytes("par");
        uint parValue = (uint)parBytes[0] | ((uint)parBytes[1] << 8) | ((uint)parBytes[2] << 16);

        Assert.That(parValue, Is.EqualTo(0x726170u), "Hex constant for 'par' should match");
    }

    [Test]
    public void TryGetProtocolCode_ValidatesHexConstants_Ndm()
    {
        byte[] ndmBytes = Encoding.UTF8.GetBytes("ndm");
        uint ndmValue = (uint)ndmBytes[0] | ((uint)ndmBytes[1] << 8) | ((uint)ndmBytes[2] << 16);

        Assert.That(ndmValue, Is.EqualTo(0x6D646Eu), "Hex constant for 'ndm' should match");
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
        ushort aaValue = (ushort)(aaBytes[0] | (aaBytes[1] << 8));

        Assert.That(aaValue, Is.EqualTo(0x6161), "Hex constant for 'aa' should match");
    }

    [Test]
    public void TryGetProtocolCode_Length6_ReturnsFalse()
    {
        byte[] protocolBytes = Encoding.UTF8.GetBytes("abcdef");
        bool result = ProtocolParser.TryGetProtocolCode(protocolBytes, out string? protocol);

        Assert.That(result, Is.False);
        Assert.That(protocol, Is.Null);
    }

    [Test]
    public void TryGetProtocolCode_Length7_ReturnsFalse()
    {
        byte[] protocolBytes = Encoding.UTF8.GetBytes("abcdefg");
        bool result = ProtocolParser.TryGetProtocolCode(protocolBytes, out string? protocol);

        Assert.That(result, Is.False);
        Assert.That(protocol, Is.Null);
    }

    [Test]
    public void TryGetProtocolCode_Length9_ReturnsFalse()
    {
        byte[] protocolBytes = Encoding.UTF8.GetBytes("abcdefghi");
        bool result = ProtocolParser.TryGetProtocolCode(protocolBytes, out string? protocol);

        Assert.That(result, Is.False);
        Assert.That(protocol, Is.Null);
    }

    [Test]
    public void TryGetProtocolCode_WithSpan_Eth_ReturnsTrue()
    {
        ReadOnlySpan<byte> protocolSpan = "eth"u8;
        bool result = ProtocolParser.TryGetProtocolCode(protocolSpan, out string? protocol);

        Assert.That(result, Is.True);
        Assert.That(protocol, Is.EqualTo(Protocol.Eth));
    }

    [Test]
    public void TryGetProtocolCode_WithSpan_Snap_ReturnsTrue()
    {
        ReadOnlySpan<byte> protocolSpan = "snap"u8;
        bool result = ProtocolParser.TryGetProtocolCode(protocolSpan, out string? protocol);

        Assert.That(result, Is.True);
        Assert.That(protocol, Is.EqualTo(Protocol.Snap));
    }

    [Test]
    public void TryGetProtocolCode_WithSpan_NodeData_ReturnsTrue()
    {
        ReadOnlySpan<byte> protocolSpan = "nodedata"u8;
        bool result = ProtocolParser.TryGetProtocolCode(protocolSpan, out string? protocol);

        Assert.That(result, Is.True);
        Assert.That(protocol, Is.EqualTo(Protocol.NodeData));
    }

    [Test]
    public void TryGetProtocolCode_PartialMatchShouldFail_EthX()
    {
        byte[] protocolBytes = Encoding.UTF8.GetBytes("ethx");
        bool result = ProtocolParser.TryGetProtocolCode(protocolBytes, out string? protocol);

        Assert.That(result, Is.False);
        Assert.That(protocol, Is.Null);
    }

    [Test]
    public void TryGetProtocolCode_PartialMatchShouldFail_Sna()
    {
        byte[] protocolBytes = Encoding.UTF8.GetBytes("sna");
        bool result = ProtocolParser.TryGetProtocolCode(protocolBytes, out string? protocol);

        Assert.That(result, Is.False);
        Assert.That(protocol, Is.Null);
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;
using NUnit.Framework;
using G2Affine = Nethermind.Crypto.Bls.P2Affine;

namespace Nethermind.Evm.ZkEvm.Test;

/// <summary>
/// The BLS12-381 point layouts the guests hand their accelerators. SP1's libzkevm parses points as zkcrypto's
/// uncompressed encoding, which blst reads too, so blst stands in for SP1 here.
/// </summary>
public class GuestBls12381LayoutTests
{
    // The G2 point of the second pair of mainnet block 26118804's BLS12-381 pairing check, as EIP-2537 encodes it.
    // SP1 rejected it as off the curve, so the block failed there and passed on ZisK.
    private static readonly byte[] G2 = Bytes.FromHexString(
        "000000000000000000000000000000000b3ddfd6ca2575d3fe5cc28da07ff6c0a3ec165ad57bde13575601fb524bc7ee87b238cdcc157be8fb91d357db738ab2" +
        "0000000000000000000000000000000006a18bd2b90251420036a564753e12eecfc5031d1405b9536c0827892d4b30365964765ffdf10ad297d5cedc26d2e652" +
        "000000000000000000000000000000000583ded9163db641c54724a49bc60efaeaff17e804e7758bee88bc502142041439956891013ee8b2571031f31c648053" +
        "00000000000000000000000000000000118164b653f13c3483920d1b0c9fcc03c3118c3ff686e8bc21e13802ac213ad8172b41737cd5c13661975cc44500cd73");

    // The negated G1 generator, paired with it.
    private static readonly byte[] G1 = Bytes.FromHexString(
        "0000000000000000000000000000000017f1d3a73197d7942695638c4fa9ac0fc3688c4f9774b905a14e3a3f171bac586c55e83ff97a1aeffb3af00adb22c6bb" +
        "00000000000000000000000000000000114d1d6855d545a8aa7d76c8cf2e21f267816aef1db507c96655b9d5caac42364e6f38ba0ecb751bad54dcd6b939c2ca");

    [Test]
    public void Sp1_layout_of_a_G2_point_is_its_zkcrypto_encoding()
    {
        byte[] decoded = DecodeG2<OnFlag>(G2);

        Assert.That(new G2Affine().TryDecode(decoded, out _), Is.True);
    }

    [Test]
    public void Eip2537_layout_of_a_G2_point_is_not_its_zkcrypto_encoding()
    {
        byte[] decoded = DecodeG2<OffFlag>(G2);

        Assert.That(new G2Affine().TryDecode(decoded, out _), Is.False);
    }

    [Test]
    public void Sp1_layout_of_the_G2_point_at_infinity_is_flagged()
    {
        byte[] decoded = DecodeG2<OnFlag>(new byte[Eip2537.LenG2]);

        G2Affine point = new();
        Assert.That(point.TryDecode(decoded, out _), Is.True);
        Assert.That(point.IsInf(), Is.True);
    }

    [Test]
    public void Sp1_layout_of_the_G1_point_at_infinity_is_flagged()
    {
        byte[] decoded = new byte[Eip2537.LenG1Trimmed];

        Assert.That(Eip2537.TryDecodeG1<OnFlag>(new byte[Eip2537.LenG1], decoded), Is.True);
        Assert.That(decoded[0], Is.EqualTo(Eip2537.Sp1InfinityBit));
        Assert.That(decoded.AsSpan(1).ContainsAnyExcept((byte)0), Is.False);
    }

    [Test]
    public void Sp1_layout_of_an_Fp2_puts_c1_first()
    {
        byte[] wire = new byte[2 * Eip2537.LenFp];
        wire[Eip2537.LenFp - 1] = 1;
        wire[2 * Eip2537.LenFp - 1] = 2;
        byte[] decoded = new byte[2 * Eip2537.LenFpTrimmed];

        Assert.That(Eip2537.TryDecodeFp2<OnFlag>(wire, decoded), Is.True);
        Assert.That(decoded[Eip2537.LenFpTrimmed - 1], Is.EqualTo(2));
        Assert.That(decoded[2 * Eip2537.LenFpTrimmed - 1], Is.EqualTo(1));
    }

    [Test]
    public void G1_round_trips_through_either_layout([Values] bool sp1, [Values] bool infinity)
    {
        byte[] wire = infinity ? new byte[Eip2537.LenG1] : G1;

        byte[] decoded = new byte[Eip2537.LenG1Trimmed];
        byte[] encoded = new byte[Eip2537.LenG1];
        if (sp1)
        {
            Assert.That(Eip2537.TryDecodeG1<OnFlag>(wire, decoded), Is.True);
            Eip2537.EncodeG1<OnFlag>(decoded, encoded);
        }
        else
        {
            Assert.That(Eip2537.TryDecodeG1<OffFlag>(wire, decoded), Is.True);
            Eip2537.EncodeG1<OffFlag>(decoded, encoded);
        }

        Assert.That(encoded, Is.EqualTo(wire));
    }

    [Test]
    public void G2_round_trips_through_either_layout([Values] bool sp1, [Values] bool infinity)
    {
        byte[] wire = infinity ? new byte[Eip2537.LenG2] : G2;

        byte[] encoded = new byte[Eip2537.LenG2];
        if (sp1)
            Eip2537.EncodeG2<OnFlag>(DecodeG2<OnFlag>(wire), encoded);
        else
            Eip2537.EncodeG2<OffFlag>(DecodeG2<OffFlag>(wire), encoded);

        Assert.That(encoded, Is.EqualTo(wire));
    }

    private static byte[] DecodeG2<TSp1Layout>(byte[] wire) where TSp1Layout : struct, IFlag
    {
        byte[] decoded = new byte[Eip2537.LenG2Trimmed];
        Assert.That(Eip2537.TryDecodeG2<TSp1Layout>(wire, decoded), Is.True);
        return decoded;
    }
}

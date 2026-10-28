// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test;

/// <summary>
/// The BLS12-381 point layout the guests hand their accelerators: EIP-2537's, with each coordinate's padding
/// trimmed.
/// </summary>
public class GuestBls12381LayoutTests
{
    // The G2 point of the second pair of mainnet block 26118804's BLS12-381 pairing check, as EIP-2537 encodes it.
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
    public void G1_round_trips([Values] bool infinity)
    {
        byte[] wire = infinity ? new byte[Eip2537.LenG1] : G1;

        byte[] decoded = new byte[Eip2537.LenG1Trimmed];
        byte[] encoded = new byte[Eip2537.LenG1];
        Assert.That(Eip2537.TryDecodeG1(wire, decoded), Is.True);
        Eip2537.EncodeG1(decoded, encoded);

        Assert.That(encoded, Is.EqualTo(wire));
    }

    [Test]
    public void G2_round_trips([Values] bool infinity)
    {
        byte[] wire = infinity ? new byte[Eip2537.LenG2] : G2;

        byte[] decoded = new byte[Eip2537.LenG2Trimmed];
        byte[] encoded = new byte[Eip2537.LenG2];
        Assert.That(Eip2537.TryDecodeG2(wire, decoded), Is.True);
        Eip2537.EncodeG2(decoded, encoded);

        Assert.That(encoded, Is.EqualTo(wire));
    }
}

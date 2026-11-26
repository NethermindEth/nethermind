// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Precompiles;
using NUnit.Framework;

namespace Nethermind.Core.Test;

/// <remarks>These constants are byte literals so ILC can precompute them; the hex is the reference.</remarks>
public class ByteLiteralConstantsTests
{
    [TestCaseSource(nameof(AddressCases))]
    public void Address_literal_matches_hex(Address address, string hex) =>
        Assert.That(address, Is.EqualTo(new Address(hex)));

    [TestCaseSource(nameof(HashCases))]
    public void Hash_literal_matches_hex(ValueHash256 hash, string hex) =>
        Assert.That(hash, Is.EqualTo(new ValueHash256(hex)));

    [Test]
    public void Eip2935_code_literal_matches_hex() =>
        Assert.That(Eip2935Constants.Code, Is.EqualTo(Bytes.FromHexString(
            "3373fffffffffffffffffffffffffffffffffffffffe14604657602036036042575f35600143038111604257611fff81430311604257611fff9006545f5260205ff35b5f5ffd5b5f35611fff60014303065500")));

    [Test]
    public void Precompile_address_literals_match_their_numbers()
    {
        Address[] addresses =
        [
            PrecompiledAddresses.ECRecover, PrecompiledAddresses.Sha256, PrecompiledAddresses.Ripemd160,
            PrecompiledAddresses.Identity, PrecompiledAddresses.ModExp, PrecompiledAddresses.BN254Add,
            PrecompiledAddresses.BN254Mul, PrecompiledAddresses.BN254PairingCheck, PrecompiledAddresses.Blake2F,
            PrecompiledAddresses.PointEvaluation, PrecompiledAddresses.Bls12381G1Add, PrecompiledAddresses.Bls12381G1Msm,
            PrecompiledAddresses.Bls12381G2Add, PrecompiledAddresses.Bls12381G2Msm, PrecompiledAddresses.Bls12381PairingCheck,
            PrecompiledAddresses.Bls12381FpToG1, PrecompiledAddresses.Bls12381Fp2ToG2
        ];

        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < addresses.Length; i++)
            {
                Assert.That(addresses[i], Is.EqualTo(Address.FromNumber((ulong)i + 1)));
            }

            Assert.That(PrecompiledAddresses.P256Verify, Is.EqualTo(Address.FromNumber(0x0100)));
        }
    }

    private static IEnumerable<TestCaseData> AddressCases()
    {
        yield return new TestCaseData(Eip2935Constants.BlockHashHistoryAddress, "0x0000F90827F1C53a10cb7A02335B175320002935").SetName("Eip2935");
        yield return new TestCaseData(Eip4788Constants.BeaconRootsAddress, "0x000F3df6D732807Ef1319fB7B8bB8522d0Beac02").SetName("Eip4788");
        yield return new TestCaseData(Eip6110Constants.MainnetDepositContractAddress, "0x00000000219ab540356cbb839cbe05303d7705fa").SetName("Eip6110Mainnet");
        yield return new TestCaseData(Eip6110Constants.HoleskyDepositContractAddress, "0x4242424242424242424242424242424242424242").SetName("Eip6110Holesky");
        yield return new TestCaseData(Eip6110Constants.SepoliaDepositContractAddress, "0x7f02c3e3c98b133055b8b348b2ac625669ed295d").SetName("Eip6110Sepolia");
        yield return new TestCaseData(Eip6110Constants.HoodiDepositContractAddress, "0x00000000219ab540356cBB839Cbe05303d7705Fa").SetName("Eip6110Hoodi");
        yield return new TestCaseData(Eip6110Constants.GnosisDepositContractAddress, "0x0B98057eA310F4d31F2a452B414647007d1645d9").SetName("Eip6110Gnosis");
        yield return new TestCaseData(Eip6110Constants.ChiadoDepositContractAddress, "0xb97036A26259B7147018913bD58a774cf91acf25").SetName("Eip6110Chiado");
        yield return new TestCaseData(Eip7002Constants.WithdrawalRequestPredeployAddress, "0x00000961Ef480Eb55e80D19ad83579A64c007002").SetName("Eip7002");
        yield return new TestCaseData(Eip7251Constants.ConsolidationRequestPredeployAddress, "0x0000BBdDc7CE488642fb579F8B00f3a590007251").SetName("Eip7251");
    }

    private static IEnumerable<TestCaseData> HashCases()
    {
        yield return new TestCaseData(Eip7002Constants.CodeHash, "0x0345a365d2f4c5975b9f1599abe0a2ee76b7a3a731bc68781bd04c84e4858f50").SetName("Eip7002");
        yield return new TestCaseData(Eip7251Constants.CodeHash, "0x78c6cb5202685228bbcbfb992b1c4e116c7ec5ef11e25b8e92716cfc628ddd60").SetName("Eip7251");
        yield return new TestCaseData(Eip8282Constants.BuilderDepositCodeHash, "0x1dd29c1e0dbc3ab670d229dbd3438003ec9015c1df9058beeb64ff301b60b98d").SetName("Eip8282Deposit");
        yield return new TestCaseData(Eip8282Constants.BuilderExitCodeHash, "0x90a0b24eb190d6c50f00f6f751dc4c2778658abf3631aceb80586c43f8bd9f2f").SetName("Eip8282Exit");
    }
}

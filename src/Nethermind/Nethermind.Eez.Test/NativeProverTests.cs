// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Net;
using Google.Protobuf;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Execution.Settlement;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class NativeProverTests
{
    private TempPath _directory = null!;
    private AttesterOptions _options = null!;
    private StreamingOptions _settings = null!;
    private EezSettlementContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = TempPath.GetTempDirectory();
        Directory.CreateDirectory(_directory.Path);
        string l2 = Path.Combine(_directory.Path, "l2.json"), l1 = Path.Combine(_directory.Path, "l1.json");
        File.WriteAllText(l2, "{\"chainId\":2}");
        File.WriteAllText(l1, "{\"chainId\":1}");
        _context = new EezSettlementContext(1, 2, TestItem.AddressC, TestItem.KeccakA, 1);
        _options = new AttesterOptions(new IPEndPoint(IPAddress.Loopback, 0), l2, 1, TestItem.KeccakA, TestItem.AddressD,
            TestItem.AddressC, "", "", 1, _context.GasLimit, WindowLimits.Default, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2));
        _settings = new StreamingOptions(new IPEndPoint(IPAddress.Loopback, 0), null, null, 1, TestItem.AddressA, TestItem.AddressB,
            "", "", [], new StreamingRegistryLimits(2, 2, 16 * 1024 * 1024, TimeSpan.FromMinutes(10)),
            new StreamingSessionLimits(2, 1024 * 1024, 100),
            new StreamingTransportLimits(2, 1024 * 1024, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)))
        { Profile = StreamingSettlementProfile.RollupXNative, L1ChainConfigPath = l1, L2GenesisHash = TestItem.KeccakB };
    }

    [TearDown]
    public void TearDown() => _directory.Dispose();

    [Test]
    public void ValidationIdentity_IdenticalInputs_IsStable()
    {
        ByteString first = NativeProver.ValidationIdentity(_options, _settings, _context);

        Assert.That(NativeProver.ValidationIdentity(_options, _settings, _context), Is.EqualTo(first),
            "capabilities must identify the same local validator and trusted deployment across calls");
    }

    [TestCase("l1Document", TestName = "ValidationIdentity_ChangedL1Document_Changes")]
    [TestCase("l2Document", TestName = "ValidationIdentity_ChangedL2Document_Changes")]
    [TestCase("l1Chain", TestName = "ValidationIdentity_ChangedL1Chain_Changes")]
    [TestCase("l2Chain", TestName = "ValidationIdentity_ChangedL2Chain_Changes")]
    [TestCase("rollup", TestName = "ValidationIdentity_ChangedRollup_Changes")]
    [TestCase("registry", TestName = "ValidationIdentity_ChangedRegistry_Changes")]
    [TestCase("manager", TestName = "ValidationIdentity_ChangedManager_Changes")]
    [TestCase("genesis", TestName = "ValidationIdentity_ChangedGenesis_Changes")]
    [TestCase("proofSystem", TestName = "ValidationIdentity_ChangedProofSystem_Changes")]
    [TestCase("vkey", TestName = "ValidationIdentity_ChangedVerificationKey_Changes")]
    [TestCase("cadence", TestName = "ValidationIdentity_ChangedCadence_Changes")]
    [TestCase("gas", TestName = "ValidationIdentity_ChangedGasLimit_Changes")]
    public void ValidationIdentity_ChangedTrustedInput_IsDifferent(string mutation)
    {
        ByteString original = NativeProver.ValidationIdentity(_options, _settings, _context);
        switch (mutation)
        {
            case "l1Document": File.AppendAllText(_settings.L1ChainConfigPath!, " "); break;
            case "l2Document": File.AppendAllText(_options.ChainConfigPath, " "); break;
            case "l1Chain": _settings = _settings with { L1ChainId = 3 }; break;
            case "l2Chain": _context = _context with { ChainId = 3 }; break;
            case "rollup": _context = _context with { RollupId = 3 }; break;
            case "registry": _settings = _settings with { Registry = TestItem.AddressD }; break;
            case "manager": _settings = _settings with { Manager = TestItem.AddressD }; break;
            case "genesis": _settings = _settings with { L2GenesisHash = TestItem.KeccakC }; break;
            case "proofSystem": _context = _context with { ProofSystem = TestItem.AddressD }; break;
            case "vkey": _context = _context with { VerificationKey = TestItem.KeccakC }; break;
            case "cadence": _context = _context with { BlockTimeSeconds = 3 }; break;
            case "gas": _context = _context with { GasLimit = _context.GasLimit + 1 }; break;
        }

        Assert.That(NativeProver.ValidationIdentity(_options, _settings, _context), Is.Not.EqualTo(original),
            "a different trusted profile cannot reuse an identity negotiated for another deployment");
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public class RollupXHeaderValidatorTests
{
    private const ulong GenesisTimestamp = 1_790_868_310;
    private static readonly EezChainRules Rules = EezChainRules.RollupX(new RollupXSchedule(1, 5,
        new RollupXL1Reference(10200, 1, TestItem.KeccakA, GenesisTimestamp, TestItem.KeccakB), GenesisTimestamp));

    private IHeaderValidator _inner = null!;
    private RollupXHeaderValidator _validator = null!;
    private BlockHeader _parent = null!;

    [SetUp]
    public void Setup()
    {
        _inner = Substitute.For<IHeaderValidator>();
        _inner.Validate(default!, default!, default, out _).ReturnsForAnyArgs(true);
        _inner.ValidateOrphaned(default!, out _).ReturnsForAnyArgs(true);
        _validator = new RollupXHeaderValidator(_inner, Rules);
        _parent = Header();
    }

    [Test]
    public void Validate_ProfileHeader_DefersToTheEthereumRules()
    {
        BlockHeader header = Header();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_validator.Validate(header, _parent, false, out string? error), Is.True, error);
            Assert.That(_validator.ValidateOrphaned(header, out error), Is.True, error);
        }
        _inner.ReceivedWithAnyArgs(1).Validate(default!, default!, default, out _);
    }

    [Test]
    public void Validate_EthereumRuleBroken_IsRejected()
    {
        _inner.Validate(default!, default!, default, out _).ReturnsForAnyArgs(x =>
        {
            x[3] = "inner";
            return false;
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_validator.Validate(Header(), _parent, false, out string? error), Is.False, "the profile adds to the Ethereum rules, it does not replace them");
            Assert.That(error, Is.EqualTo("inner"));
        }
    }

    [TestCaseSource(nameof(BrokenProfiles))]
    public void Validate_ProfileBroken_IsRejectedBeforeTheEthereumRules(Action<BlockHeader> mutate, string reason)
    {
        BlockHeader header = Header();
        mutate(header);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_validator.Validate(header, _parent, false, out string? error), Is.False);
            Assert.That(error, Does.Contain(reason));
        }
        _inner.DidNotReceiveWithAnyArgs().Validate(default!, default!, default, out _);
    }

    [Test]
    public void Construct_Rollup0Rules_Throws() =>
        Assert.Throws<ArgumentException>(() => _ = new RollupXHeaderValidator(_inner, EezChainRules.Rollup0));

    private static TestCaseData[] BrokenProfiles() =>
    [
        Case("GasLimitDrift", static h => h.GasLimit = EezChainRules.RollupXBlockGasLimit - 1, "gas limit"),
        Case("OffGridTimestamp", static h => h.Timestamp++, "timestamp"),
        Case("Ommers", static h => h.UnclesHash = TestItem.KeccakC, "ommers"),
        Case("Difficulty", static h => h.Difficulty = 1, "difficulty"),
        Case("Nonce", static h => h.Nonce = 1, "nonce"),
        Case("WithdrawalsRoot", static h => h.WithdrawalsRoot = TestItem.KeccakC, "withdrawals root"),
        Case("MissingWithdrawalsRoot", static h => h.WithdrawalsRoot = null, "withdrawals root"),
        Case("BeaconRoot", static h => h.ParentBeaconBlockRoot = TestItem.KeccakC, "beacon root"),
        Case("MissingBeaconRoot", static h => h.ParentBeaconBlockRoot = null, "beacon root"),
        Case("RequestsHash", static h => h.RequestsHash = TestItem.KeccakC, "requests hash"),
        Case("BlobGasUsed", static h => h.BlobGasUsed = 131_072, "blob gas"),
        Case("ExcessBlobGas", static h => h.ExcessBlobGas = 1, "blob gas"),
        Case("MissingBlobGas", static h => h.BlobGasUsed = null, "blob gas"),
        Case("BlockAccessList", static h => h.BlockAccessListHash = TestItem.KeccakC, "block access list"),
        Case("SlotNumber", static h => h.SlotNumber = 1, "slot number"),
    ];

    private static TestCaseData Case(string name, Action<BlockHeader> mutate, string reason) => new TestCaseData(mutate, reason).SetName(name);

    private static BlockHeader Header()
    {
        BlockHeader header = Build.A.BlockHeader
            .WithNumber(7)
            .WithTimestamp(GenesisTimestamp + 7)
            .WithGasLimit(EezChainRules.RollupXBlockGasLimit)
            .WithDifficulty(0)
            .WithNonce(0)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithWithdrawalsRoot(Keccak.EmptyTreeHash)
            .WithParentBeaconBlockRoot(Keccak.Zero)
            .WithRequestsHash(ExecutionRequestExtensions.EmptyRequestsHash)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .TestObject;
        return header;
    }
}

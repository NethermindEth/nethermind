// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Taiko.Rpc;
using Nethermind.Taiko.TaikoSpec;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

/// <summary>
/// Unit tests for <see cref="TaikoHeaderValidator"/> covering Unzen blob-gas field
/// validation, requests-hash pinning, difficulty rules, and blob-transaction rejection.
/// These mirror the coverage in alethia-reth's
/// <c>crates/consensus/src/validation/tests.rs</c>.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class TaikoHeaderValidatorTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Creates a <see cref="TaikoHeaderValidator"/> backed by a stub tree and the given spec provider.</summary>
    private static TaikoHeaderValidator MakeValidator(ISpecProvider specProvider)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        ISealValidator sealValidator = Always.Valid;
        IL1OriginStore l1OriginStore = Substitute.For<IL1OriginStore>();
        return new TaikoHeaderValidator(blockTree, sealValidator, specProvider, l1OriginStore, Timestamper.Default, LimboLogs.Instance);
    }

    /// <summary>Creates a spec provider that returns <paramref name="spec"/> for every block.</summary>
    private static ISpecProvider ProviderFor(ITaikoReleaseSpec spec) => new TestSpecProvider(spec);

    /// <summary>Builds a minimal valid non-Unzen parent header.</summary>
    private static BlockHeader ParentWithBaseFee(ulong baseFee = 1) =>
        Build.A.BlockHeader
            .WithNumber(0)
            .WithTimestamp(0)
            .WithBaseFee(baseFee)
            .TestObject;
    /// <summary>
    /// Minimum extra-data for Shasta/Unzen headers (1 base-fee-sharing byte + 6 proposal-ID bytes).
    /// </summary>
    private static readonly byte[] ShastaExtraData = new byte[TaikoHeaderHelper.ShastaExtraDataLen];
    // ── ValidateBlobGasFields (Unzen) ──────────────────────────────────────────

    /// <summary>
    /// Unzen headers must carry BlobGasUsed = 0; a null value is rejected.
    /// Mirrors <c>test_rejects_blob_transactions</c> enforcement that Unzen blocks have
    /// no blob activity whatsoever.
    /// </summary>
    [Test]
    public void Unzen_RejectsBlobGasUsed_WhenNull()
    {
        TaikoUnzenReleaseSpec spec = new();
        ISpecProvider provider = ProviderFor(spec);
        TaikoHeaderValidator validator = MakeValidator(provider);

        BlockHeader parent = ParentWithBaseFee();
        BlockHeader header = Build.A.BlockHeader
            .WithNumber(1)
            .WithParent(parent)
            .WithTimestamp(1)
            .WithBaseFee(25_000_000)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithWithdrawalsRoot(Keccak.EmptyTreeHash)
            .WithRequestsHash(ExecutionRequestExtensions.EmptyRequestsHash)
            // BlobGasUsed intentionally left null
            .WithExcessBlobGas(0)
            .WithExtraData(new byte[TaikoHeaderHelper.ShastaExtraDataLen])
            .WithParentBeaconBlockRoot(Keccak.Zero)
            .TestObject;

        bool valid = validator.Validate(header, parent, isUncle: false, out string? error);

        Assert.That(valid, Is.False);
        Assert.That(error, Does.Contain("BlobGasUsed"));
    }

    /// <summary>Unzen headers must carry BlobGasUsed = 0; any nonzero value is rejected.</summary>
    [Test]
    public void Unzen_RejectsBlobGasUsed_WhenNonZero()
    {
        TaikoUnzenReleaseSpec spec = new();
        ISpecProvider provider = ProviderFor(spec);
        TaikoHeaderValidator validator = MakeValidator(provider);

        BlockHeader parent = ParentWithBaseFee();
        BlockHeader header = Build.A.BlockHeader
            .WithNumber(1)
            .WithParent(parent)
            .WithTimestamp(1)
            .WithBaseFee(25_000_000)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithWithdrawalsRoot(Keccak.EmptyTreeHash)
            .WithRequestsHash(ExecutionRequestExtensions.EmptyRequestsHash)
            .WithBlobGasUsed(1)
            .WithExcessBlobGas(0)
            .WithExtraData(new byte[TaikoHeaderHelper.ShastaExtraDataLen])
            .WithParentBeaconBlockRoot(Keccak.Zero)
            .TestObject;

        bool valid = validator.Validate(header, parent, isUncle: false, out string? error);

        Assert.That(valid, Is.False);
        Assert.That(error, Does.Contain("BlobGasUsed"));
    }

    /// <summary>Unzen headers must carry ExcessBlobGas = 0; a null value is rejected.</summary>
    [Test]
    public void Unzen_RejectsExcessBlobGas_WhenNull()
    {
        TaikoUnzenReleaseSpec spec = new();
        ISpecProvider provider = ProviderFor(spec);
        TaikoHeaderValidator validator = MakeValidator(provider);

        BlockHeader parent = ParentWithBaseFee();
        BlockHeader header = Build.A.BlockHeader
            .WithNumber(1)
            .WithParent(parent)
            .WithTimestamp(1)
            .WithBaseFee(25_000_000)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithWithdrawalsRoot(Keccak.EmptyTreeHash)
            .WithRequestsHash(ExecutionRequestExtensions.EmptyRequestsHash)
            .WithBlobGasUsed(0)
            // ExcessBlobGas intentionally left null
            .WithExtraData(new byte[TaikoHeaderHelper.ShastaExtraDataLen])
            .WithParentBeaconBlockRoot(Keccak.Zero)
            .TestObject;

        bool valid = validator.Validate(header, parent, isUncle: false, out string? error);

        Assert.That(valid, Is.False);
        Assert.That(error, Does.Contain("ExcessBlobGas"));
    }

    /// <summary>Unzen headers must carry ExcessBlobGas = 0; any nonzero value is rejected.</summary>
    [Test]
    public void Unzen_RejectsExcessBlobGas_WhenNonZero()
    {
        TaikoUnzenReleaseSpec spec = new();
        ISpecProvider provider = ProviderFor(spec);
        TaikoHeaderValidator validator = MakeValidator(provider);

        BlockHeader parent = ParentWithBaseFee();
        BlockHeader header = Build.A.BlockHeader
            .WithNumber(1)
            .WithParent(parent)
            .WithTimestamp(1)
            .WithBaseFee(25_000_000)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithWithdrawalsRoot(Keccak.EmptyTreeHash)
            .WithRequestsHash(ExecutionRequestExtensions.EmptyRequestsHash)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(1)
            .WithExtraData(new byte[TaikoHeaderHelper.ShastaExtraDataLen])
            .WithParentBeaconBlockRoot(Keccak.Zero)
            .TestObject;

        bool valid = validator.Validate(header, parent, isUncle: false, out string? error);

        Assert.That(valid, Is.False);
        Assert.That(error, Does.Contain("ExcessBlobGas"));
    }

    /// <summary>Unzen header must have ParentBeaconBlockRoot set to
    /// <see cref="Keccak.Zero"/>; a null value is rejected.</summary>
    [Test]
    public void Unzen_RejectsParentBeaconBlockRoot_WhenNull()
    {
        TaikoUnzenReleaseSpec spec = new();
        ISpecProvider provider = ProviderFor(spec);
        TaikoHeaderValidator validator = MakeValidator(provider);

        BlockHeader parent = ParentWithBaseFee();
        BlockHeader header = Build.A.BlockHeader
            .WithNumber(1)
            .WithParent(parent)
            .WithTimestamp(1)
            .WithBaseFee(25_000_000)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithWithdrawalsRoot(Keccak.EmptyTreeHash)
            .WithRequestsHash(ExecutionRequestExtensions.EmptyRequestsHash)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithExtraData(new byte[TaikoHeaderHelper.ShastaExtraDataLen])
            // ParentBeaconBlockRoot intentionally left null
            .TestObject;

        bool valid = validator.Validate(header, parent, isUncle: false, out string? error);

        Assert.That(valid, Is.False);
        Assert.That(error, Does.Contain("ParentBeaconBlockRoot"));
    }

    /// <summary>Unzen header must have ParentBeaconBlockRoot exactly equal to
    /// <see cref="Keccak.Zero"/>; any other value is rejected.</summary>
    [Test]
    public void Unzen_RejectsParentBeaconBlockRoot_WhenNonZero()
    {
        TaikoUnzenReleaseSpec spec = new();
        ISpecProvider provider = ProviderFor(spec);
        TaikoHeaderValidator validator = MakeValidator(provider);

        BlockHeader parent = ParentWithBaseFee();
        BlockHeader header = Build.A.BlockHeader
            .WithNumber(1)
            .WithParent(parent)
            .WithTimestamp(1)
            .WithBaseFee(25_000_000)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithWithdrawalsRoot(Keccak.EmptyTreeHash)
            .WithRequestsHash(ExecutionRequestExtensions.EmptyRequestsHash)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithExtraData(new byte[TaikoHeaderHelper.ShastaExtraDataLen])
            .WithParentBeaconBlockRoot(TestItem.KeccakA)
            .TestObject;

        bool valid = validator.Validate(header, parent, isUncle: false, out string? error);

        Assert.That(valid, Is.False);
        Assert.That(error, Does.Contain("ParentBeaconBlockRoot"));
    }

    // ── ValidateRequestsHash ──────────────────────────────────────────────────

    /// <summary>
    /// Unzen headers must pin RequestsHash to the canonical empty-requests value.
    /// A null RequestsHash is rejected.
    /// Mirrors <c>unzen_header_allows_nonzero_difficulty</c> and the requests-hash
    /// test cases in alethia-reth.
    /// </summary>
    [Test]
    public void Unzen_RejectsRequestsHash_WhenNull()
    {
        TaikoUnzenReleaseSpec spec = new();
        ISpecProvider provider = ProviderFor(spec);
        TaikoHeaderValidator validator = MakeValidator(provider);

        BlockHeader parent = ParentWithBaseFee();
        BlockHeader header = Build.A.BlockHeader
            .WithNumber(1)
            .WithParent(parent)
            .WithTimestamp(1)
            .WithBaseFee(25_000_000)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithWithdrawalsRoot(Keccak.EmptyTreeHash)
            // RequestsHash intentionally left null
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithExtraData(new byte[TaikoHeaderHelper.ShastaExtraDataLen])
            .WithParentBeaconBlockRoot(Keccak.Zero)
            .TestObject;

        bool valid = validator.Validate(header, parent, isUncle: false, out string? error);

        Assert.That(valid, Is.False);
        Assert.That(error, Does.Contain("RequestsHash"));
    }

    /// <summary>Unzen headers whose RequestsHash differs from the canonical empty value are rejected.</summary>
    [Test]
    public void Unzen_RejectsRequestsHash_WhenNotEmptyRequestsHash()
    {
        TaikoUnzenReleaseSpec spec = new();
        ISpecProvider provider = ProviderFor(spec);
        TaikoHeaderValidator validator = MakeValidator(provider);

        BlockHeader parent = ParentWithBaseFee();
        BlockHeader header = Build.A.BlockHeader
            .WithNumber(1)
            .WithParent(parent)
            .WithTimestamp(1)
            .WithBaseFee(25_000_000)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithWithdrawalsRoot(Keccak.EmptyTreeHash)
            .WithRequestsHash(TestItem.KeccakB)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithExtraData(new byte[TaikoHeaderHelper.ShastaExtraDataLen])
            .WithParentBeaconBlockRoot(Keccak.Zero)
            .TestObject;

        bool valid = validator.Validate(header, parent, isUncle: false, out string? error);

        Assert.That(valid, Is.False);
        Assert.That(error, Does.Contain("RequestsHash"));
    }

    /// <summary>Unzen headers whose RequestsHash equals the canonical empty value are accepted.</summary>
    [Test]
    public void Unzen_AcceptsRequestsHash_WhenEmptyRequestsHash()
    {
        TaikoUnzenReleaseSpec spec = new();
        ISpecProvider provider = ProviderFor(spec);
        TaikoHeaderValidator validator = MakeValidator(provider);

        BlockHeader parent = ParentWithBaseFee();
        BlockHeader header = Build.A.BlockHeader
            .WithNumber(1)
            .WithParent(parent)
            .WithTimestamp(1)
            .WithBaseFee(25_000_000)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithWithdrawalsRoot(Keccak.EmptyTreeHash)
            .WithRequestsHash(ExecutionRequestExtensions.EmptyRequestsHash)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithExtraData(new byte[TaikoHeaderHelper.ShastaExtraDataLen])
            .WithParentBeaconBlockRoot(Keccak.Zero)
            .TestObject;

        bool valid = validator.Validate(header, parent, isUncle: false, out _);

        Assert.That(valid, Is.True);
    }

    // ── Difficulty rules ──────────────────────────────────────────────────────

    /// <summary>
    /// Pre-Unzen headers must have Difficulty = 0.
    /// Mirrors <c>pre_unzen_header_still_rejects_nonzero_difficulty</c>.
    /// </summary>
    [Test]
    public void PreUnzen_RejectsNonZeroDifficulty()
    {
        TaikoShastaReleaseSpec spec = new();
        ISpecProvider provider = ProviderFor(spec);
        TaikoHeaderValidator validator = MakeValidator(provider);

        BlockHeader parent = ParentWithBaseFee();
        BlockHeader header = Build.A.BlockHeader
            .WithNumber(1)
            .WithParent(parent)
            .WithTimestamp(1)
            .WithBaseFee(25_000_000)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithWithdrawalsRoot(Keccak.EmptyTreeHash)
            .WithExtraData(new byte[TaikoHeaderHelper.ShastaExtraDataLen])
            .WithDifficulty(1)
            .TestObject;

        bool valid = validator.Validate(header, parent, isUncle: false, out string? error);

        Assert.That(valid, Is.False);
        Assert.That(error, Does.Contain("difficulty").Or.Contain("Difficulty"));
    }

    /// <summary>
    /// Unzen headers may carry a nonzero Difficulty (used to encode finalized ZK-gas).
    /// Mirrors <c>unzen_header_allows_nonzero_difficulty</c>.
    /// </summary>
    [Test]
    public void Unzen_AcceptsNonZeroDifficulty()
    {
        TaikoUnzenReleaseSpec spec = new();
        ISpecProvider provider = ProviderFor(spec);
        TaikoHeaderValidator validator = MakeValidator(provider);

        BlockHeader parent = ParentWithBaseFee();
        BlockHeader header = Build.A.BlockHeader
            .WithNumber(1)
            .WithParent(parent)
            .WithTimestamp(1)
            .WithBaseFee(25_000_000)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithWithdrawalsRoot(Keccak.EmptyTreeHash)
            .WithRequestsHash(ExecutionRequestExtensions.EmptyRequestsHash)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithExtraData(new byte[TaikoHeaderHelper.ShastaExtraDataLen])
            .WithParentBeaconBlockRoot(Keccak.Zero)
            .WithDifficulty(7)
            .TestObject;

        bool valid = validator.Validate(header, parent, isUncle: false, out _);

        Assert.That(valid, Is.True);
    }

    // ── Ommer / uncle validation ──────────────────────────────────────────────

    /// <summary>
    /// Every Taiko header must present the empty-sequence uncle hash regardless of fork.
    /// Mirrors <c>test_validate_block_pre_execution_rejects_non_empty_ommer_hash</c>.
    /// </summary>
    [Test]
    public void AnyFork_RejectsNonEmptyUncleHash()
    {
        TaikoOntakeReleaseSpec spec = new();
        ISpecProvider provider = ProviderFor(spec);
        TaikoHeaderValidator validator = MakeValidator(provider);

        BlockHeader parent = ParentWithBaseFee();
        BlockHeader header = Build.A.BlockHeader
            .WithNumber(1)
            .WithParent(parent)
            .WithTimestamp(1)
            .WithBaseFee(25_000_000)
            .WithUnclesHash(TestItem.KeccakA)
            .WithWithdrawalsRoot(Keccak.EmptyTreeHash)
            .TestObject;

        bool valid = validator.Validate(header, parent, isUncle: false, out string? error);

        Assert.That(valid, Is.False);
        Assert.That(error, Does.Contain("Uncles").Or.Contain("uncles").Or.Contain("uncle"));
    }

    /// <summary>
    /// Every Taiko header must provide a WithdrawalsRoot; null is rejected.
    /// </summary>
    [Test]
    public void AnyFork_RejectsMissingWithdrawalsRoot()
    {
        TaikoOntakeReleaseSpec spec = new();
        ISpecProvider provider = ProviderFor(spec);
        TaikoHeaderValidator validator = MakeValidator(provider);

        BlockHeader parent = ParentWithBaseFee();
        BlockHeader header = Build.A.BlockHeader
            .WithNumber(1)
            .WithParent(parent)
            .WithTimestamp(1)
            .WithBaseFee(25_000_000)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            // WithdrawalsRoot intentionally left null
            .TestObject;

        bool valid = validator.Validate(header, parent, isUncle: false, out string? error);

        Assert.That(valid, Is.False);
        Assert.That(error, Does.Contain("WithdrawalsRoot").Or.Contain("withdrawals"));
    }

    /// <summary>
    /// The Shasta base fee floor is 0.01 gwei on Taiko mainnet and 0.005 gwei elsewhere, as in taiko-geth and alethia-reth.
    /// The parent mirrors a live Alethia block: 112,056 gas used against a 23M target, 2 s after its own parent.
    /// </summary>
    [TestCase(BatchLookupThresholds.TaikoMainnetChainId, 10_000_000UL, TestName = "Shasta base fee on Taiko mainnet is clamped to 0.01 gwei")]
    [TestCase(BatchLookupThresholds.TaikoHoodiChainId, 8_756_090UL, TestName = "Shasta base fee on other Taiko chains keeps the EIP-4396 value")]
    public void Shasta_BaseFee_ClampedToChainFloor(ulong chainId, ulong expectedBaseFee)
    {
        ISpecProvider provider = new TestSpecProvider(new TaikoUnzenReleaseSpec()) { ChainId = chainId };

        BlockHeader grandParent = Build.A.BlockHeader
            .WithNumber(100)
            .WithTimestamp(1_000)
            .TestObject;
        BlockHeader parent = Build.A.BlockHeader
            .WithNumber(101)
            .WithParent(grandParent)
            .WithTimestamp(1_002)
            .WithBaseFee(10_000_000)
            .WithGasLimit(46_000_000)
            .WithGasUsed(112_056)
            .TestObject;

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(parent.ParentHash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(grandParent);
        TaikoHeaderValidator validator = new(blockTree, Always.Valid, provider, Substitute.For<IL1OriginStore>(), Timestamper.Default, LimboLogs.Instance);

        BlockHeader header = Build.A.BlockHeader
            .WithNumber(102)
            .WithParent(parent)
            .WithTimestamp(1_004)
            .WithGasLimit(46_000_000)
            .WithBaseFee(expectedBaseFee)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithWithdrawalsRoot(Keccak.EmptyTreeHash)
            .WithRequestsHash(ExecutionRequestExtensions.EmptyRequestsHash)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithExtraData(ShastaExtraData)
            .WithParentBeaconBlockRoot(Keccak.Zero)
            .TestObject;

        bool valid = validator.Validate(header, parent, isUncle: false, out string? error);

        Assert.That(valid, Is.True, error);
    }

    /// <summary>
    /// Shasta extraData is exactly basefeeSharingPctg (1 byte) followed by proposalId (6 bytes);
    /// taiko-geth and alethia-reth reject any other length. Pacaya keeps the generic 32-byte limit.
    /// </summary>
    [TestCase(typeof(TaikoShastaReleaseSpec), 6, false)]
    [TestCase(typeof(TaikoShastaReleaseSpec), 7, true)]
    [TestCase(typeof(TaikoShastaReleaseSpec), 8, false)]
    [TestCase(typeof(TaikoShastaReleaseSpec), 32, false)]
    [TestCase(typeof(TaikoUnzenReleaseSpec), 6, false)]
    [TestCase(typeof(TaikoUnzenReleaseSpec), 7, true)]
    [TestCase(typeof(TaikoUnzenReleaseSpec), 8, false)]
    [TestCase(typeof(TaikoUnzenReleaseSpec), 32, false)]
    [TestCase(typeof(TaikoPacayaReleaseSpec), 8, true)]
    [TestCase(typeof(TaikoPacayaReleaseSpec), 32, true)]
    public void Shasta_RequiresExactExtraDataLength(Type specType, int length, bool accepted)
    {
        ITaikoReleaseSpec spec = (ITaikoReleaseSpec)Activator.CreateInstance(specType)!;
        TaikoHeaderValidator validator = MakeValidator(ProviderFor(spec));

        BlockHeader parent = ParentWithBaseFee();
        BlockHeaderBuilder builder = Build.A.BlockHeader
            .WithNumber(1)
            .WithParent(parent)
            .WithTimestamp(1)
            .WithBaseFee(25_000_000)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithWithdrawalsRoot(Keccak.EmptyTreeHash)
            .WithExtraData(new byte[length])
            .WithDifficulty(0);
        if (spec.IsEip4844Enabled)
        {
            builder = builder
                .WithRequestsHash(ExecutionRequestExtensions.EmptyRequestsHash)
                .WithBlobGasUsed(0)
                .WithExcessBlobGas(0)
                .WithParentBeaconBlockRoot(Keccak.Zero);
        }

        bool valid = validator.Validate(builder.TestObject, parent, isUncle: false, out string? error);

        Assert.That(valid, Is.EqualTo(accepted), error);
        if (!accepted)
        {
            Assert.That(error, Does.Contain("ExtraData"));
        }
    }
}

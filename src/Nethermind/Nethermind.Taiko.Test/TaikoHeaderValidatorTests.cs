// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
using Nethermind.Taiko.TaikoSpec;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

/// <summary>
/// Unit tests for <see cref="TaikoHeaderValidator"/> covering Uzen blob-gas field
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

    /// <summary>Builds a minimal valid non-Uzen parent header.</summary>
    private static BlockHeader ParentWithBaseFee(ulong baseFee = 1) =>
        Build.A.BlockHeader
            .WithNumber(0)
            .WithTimestamp(0)
            .WithBaseFee(baseFee)
            .TestObject;
    /// <summary>
    /// Minimum extra-data for Shasta/Uzen headers (1 base-fee-sharing byte + 6 proposal-ID bytes).
    /// </summary>
    private static readonly byte[] ShastaExtraData = new byte[TaikoHeaderHelper.ShastaExtraDataLen];
    // ── ValidateBlobGasFields (Uzen) ──────────────────────────────────────────

    /// <summary>
    /// Uzen headers must carry BlobGasUsed = 0; a null value is rejected.
    /// Mirrors <c>test_rejects_blob_transactions</c> enforcement that Uzen blocks have
    /// no blob activity whatsoever.
    /// </summary>
    [Test]
    public void Uzen_RejectsBlobGasUsed_WhenNull()
    {
        TaikoUzenReleaseSpec spec = new();
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

    /// <summary>Uzen headers must carry BlobGasUsed = 0; any nonzero value is rejected.</summary>
    [Test]
    public void Uzen_RejectsBlobGasUsed_WhenNonZero()
    {
        TaikoUzenReleaseSpec spec = new();
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

    /// <summary>Uzen headers must carry ExcessBlobGas = 0; a null value is rejected.</summary>
    [Test]
    public void Uzen_RejectsExcessBlobGas_WhenNull()
    {
        TaikoUzenReleaseSpec spec = new();
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

    /// <summary>Uzen headers must carry ExcessBlobGas = 0; any nonzero value is rejected.</summary>
    [Test]
    public void Uzen_RejectsExcessBlobGas_WhenNonZero()
    {
        TaikoUzenReleaseSpec spec = new();
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

    /// <summary>Uzen header must have ParentBeaconBlockRoot set to
    /// <see cref="Keccak.Zero"/>; a null value is rejected.</summary>
    [Test]
    public void Uzen_RejectsParentBeaconBlockRoot_WhenNull()
    {
        TaikoUzenReleaseSpec spec = new();
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

    /// <summary>Uzen header must have ParentBeaconBlockRoot exactly equal to
    /// <see cref="Keccak.Zero"/>; any other value is rejected.</summary>
    [Test]
    public void Uzen_RejectsParentBeaconBlockRoot_WhenNonZero()
    {
        TaikoUzenReleaseSpec spec = new();
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
    /// Uzen headers must pin RequestsHash to the canonical empty-requests value.
    /// A null RequestsHash is rejected.
    /// Mirrors <c>uzen_header_allows_nonzero_difficulty</c> and the requests-hash
    /// test cases in alethia-reth.
    /// </summary>
    [Test]
    public void Uzen_RejectsRequestsHash_WhenNull()
    {
        TaikoUzenReleaseSpec spec = new();
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

    /// <summary>Uzen headers whose RequestsHash differs from the canonical empty value are rejected.</summary>
    [Test]
    public void Uzen_RejectsRequestsHash_WhenNotEmptyRequestsHash()
    {
        TaikoUzenReleaseSpec spec = new();
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

    /// <summary>Uzen headers whose RequestsHash equals the canonical empty value are accepted.</summary>
    [Test]
    public void Uzen_AcceptsRequestsHash_WhenEmptyRequestsHash()
    {
        TaikoUzenReleaseSpec spec = new();
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
    /// Pre-Uzen headers must have Difficulty = 0.
    /// Mirrors <c>pre_uzen_header_still_rejects_nonzero_difficulty</c>.
    /// </summary>
    [Test]
    public void PreUzen_RejectsNonZeroDifficulty()
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
    /// Uzen headers may carry a nonzero Difficulty (used to encode finalized ZK-gas).
    /// Mirrors <c>uzen_header_allows_nonzero_difficulty</c>.
    /// </summary>
    [Test]
    public void Uzen_AcceptsNonZeroDifficulty()
    {
        TaikoUzenReleaseSpec spec = new();
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
}

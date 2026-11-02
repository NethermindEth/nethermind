// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;
using ValidationResult = Nethermind.Merge.Plugin.Data.ValidationResult;

namespace Nethermind.Merge.Plugin.Test;

public class ProofInclusionListPayloadTests
{
    private static readonly OverridableReleaseSpec PerListSpec = new(Bogota.Instance) { IsEip8141Enabled = true, IsEip8288Enabled = true };

    private static PayloadAttributes Attributes(InclusionListV1[]? lists, byte[][]? flattened = null) => new()
    {
        Timestamp = 1,
        PrevRandao = Keccak.Zero,
        SuggestedFeeRecipient = TestItem.AddressA,
        Withdrawals = [],
        ParentBeaconBlockRoot = Keccak.Zero,
        SlotNumber = 1,
        TargetGasLimit = 30_000_000,
        InclusionListTransactions = flattened,
        InclusionLists = lists
    };

    [Test]
    public void Version_errors_precede_inclusion_list_shape_errors()
    {
        PayloadAttributes attributes = Attributes([]);
        TestSpecProvider provider = new(PerListSpec);
        PayloadAttributesValidationResult expected = attributes.Validate(provider, 4, out string? expectedError);
        attributes.InclusionLists = [new() { Transactions = [] }];
        Assert.That(attributes.Validate(provider, 4, out string? actualError), Is.EqualTo(expected));
        Assert.That(actualError, Is.EqualTo(expectedError));
    }

    // EIPs#12475: PayloadAttributesV6 carries the per-list form once EIP-8288 and EIP-7805 are both active; the flattened
    // PayloadAttributesV5 is refused there and the per-list form is refused without EIP-8288.
    [TestCase(true, 6, true, PayloadAttributesValidationResult.Success)]
    [TestCase(true, 5, false, PayloadAttributesValidationResult.UnsupportedFork)]
    [TestCase(false, 5, false, PayloadAttributesValidationResult.Success)]
    [TestCase(false, 6, true, PayloadAttributesValidationResult.UnsupportedFork)]
    public void Attributes_version_follows_the_per_list_fork(bool eip8288, int fcuVersion, bool perList, PayloadAttributesValidationResult expected)
    {
        OverridableReleaseSpec spec = eip8288 ? PerListSpec : new(Bogota.Instance);
        PayloadAttributes attributes = perList ? Attributes([]) : Attributes(null, []);
        Assert.That(attributes.Validate(new TestSpecProvider(spec), fcuVersion, out string? error), Is.EqualTo(expected), error);
    }

    [TestCase(true, 6, false)]
    [TestCase(true, 7, true)]
    [TestCase(false, 6, true)]
    [TestCase(false, 7, false)]
    public void New_payload_version_follows_the_per_list_fork(bool eip8288, int version, bool expected)
    {
        IReleaseSpec spec = eip8288 ? PerListSpec : Bogota.Instance;
        ExecutionPayloadV4 payload = new() { Timestamp = 1 };
        Assert.That(payload.ValidateForkOnNewPayload(new TestSpecProvider(spec), version), Is.EqualTo(expected));
    }

    [TestCase("missing-transactions", true)]
    [TestCase("missing-transaction", true)]
    [TestCase("too-many-lists", true)]
    [TestCase("oversized-list", true)]
    [TestCase("missing-proof", true)]
    [TestCase("missing-proof-deps", true)]
    [TestCase("short-proof-dependency", true)]
    [TestCase("oversized-proof", true)]
    // EIPs#12475: an undecodable entry or an invalid proof is scoped to its list: the entry contributes nothing and the proof
    // obligates nothing, so it must not fail the request for every other list.
    [TestCase("empty-transaction", false)]
    [TestCase("invalid-proof", false)]
    [TestCase("unsorted-proof-deps", false)]
    [TestCase("empty-proof", false)]
    public void List_shape_is_checked_at_each_entry_point(string invalidShape, bool rejected)
    {
        byte[] dependency = new byte[Eip8288Constants.DependencyTripleLength];
        dependency[31] = Eip8288Constants.LeanSphincsScheme;
        RecursiveStarkV1? proof = invalidShape switch
        {
            "missing-proof" => null,
            "missing-proof-deps" => new RecursiveStarkV1 { StarkProof = [1] },
            "short-proof-dependency" => new RecursiveStarkV1 { StarkProof = [1], ProofDeps = [dependency[..^1]] },
            "oversized-proof" => new RecursiveStarkV1 { StarkProof = new byte[Eip8288Constants.MaxIlProofBytes], ProofDeps = [] },
            "unsorted-proof-deps" => new RecursiveStarkV1 { StarkProof = [1], ProofDeps = [dependency, dependency] },
            "empty-proof" => RecursiveStarkV1.Empty,
            _ => new RecursiveStarkV1 { StarkProof = [9], ProofDeps = [dependency] }
        };
        InclusionListV1[] lists = invalidShape switch
        {
            "missing-transactions" => [new() { Transactions = null, RecursiveStark = proof }],
            "missing-transaction" => [new() { Transactions = [null!], RecursiveStark = proof }],
            "too-many-lists" => [.. Enumerable.Range(0, Eip7805Constants.InclusionListCommitteeSize + 1).Select(_ => new InclusionListV1 { Transactions = [], RecursiveStark = proof })],
            "empty-transaction" => [new() { Transactions = [[]], RecursiveStark = proof }],
            "oversized-list" => [new() { Transactions = [new byte[Eip7805Constants.MaxBytesPerInclusionList + 1]], RecursiveStark = proof }],
            _ => [new() { Transactions = [], RecursiveStark = proof }]
        };
        string? listError = InclusionListV1.Validate(lists);
        Assert.That(listError is not null, Is.EqualTo(rejected));
        Assert.That(Attributes(lists).Validate(new TestSpecProvider(PerListSpec), 6, out _),
            Is.EqualTo(rejected ? PayloadAttributesValidationResult.InvalidPayloadAttributes : PayloadAttributesValidationResult.Success));

        // The test payload fails a later, unrelated check, so an accepted list must reach the same error as a well-formed one.
        ExecutionPayloadV3 payload = ExecutionPayloadV3.Create(Build.A.Block.TestObject);
        new ExecutionPayloadParams<ExecutionPayloadV3>(payload, [], Keccak.Zero, [], null, [new() { Transactions = [], RecursiveStark = RecursiveStarkV1.Empty }])
            .ValidateParams(PerListSpec, 7, out string? wellFormedError);
        ExecutionPayloadParams<ExecutionPayloadV3> parameters = new(payload, [], Keccak.Zero, [], null, lists);
        Assert.That(parameters.ValidateParams(PerListSpec, 7, out string? error), Is.EqualTo(ValidationResult.Fail));
        Assert.That(error, Is.EqualTo(rejected ? listError : wellFormedError));
    }

    [Test]
    public void Attributes_reject_a_flattened_list_beside_the_per_list_form()
        => Assert.That(Attributes([], []).Validate(new TestSpecProvider(PerListSpec), 6, out _),
            Is.EqualTo(PayloadAttributesValidationResult.InvalidPayloadAttributes));

    [Test]
    public void Per_list_proofs_are_input_only_and_decoded_per_list()
    {
        Transaction first = Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Transaction second = Build.A.Transaction.WithNonce(2).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        InclusionListV1[] lists =
        [
            new() { Transactions = [Encode(first), [0xc0]], RecursiveStark = new() { StarkProof = [1], ProofDeps = [new byte[96]] } },
            new() { Transactions = [Encode(second)], RecursiveStark = RecursiveStarkV1.Empty }
        ];
        Block block = Build.A.Block.WithNumber(1).TestObject;
        ExecutionPayloadV3 payload = ExecutionPayloadV3.Create(block);
        Assert.That(payload.InclusionLists, Is.Null);
        payload.InclusionLists = lists;
        Result<Block> result = payload.TryGetBlock();
        Assert.That(result.IsError, Is.False, result.Error);
        Block decoded = result.Data!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.InclusionLists, Has.Count.EqualTo(2));
            Assert.That(decoded.InclusionLists![0].Transactions.Select(static tx => tx.Nonce), Is.EqualTo(new ulong[] { 1 }), "an undecodable entry is dropped");
            Assert.That(decoded.InclusionLists[0].StarkProof, Is.EqualTo(new byte[] { 1 }));
            Assert.That(decoded.InclusionLists[0].ProofDeps, Is.EqualTo(new[] { new byte[96] }));
            Assert.That(decoded.InclusionLists[1].StarkProof, Is.Empty);
            Assert.That(decoded.InclusionListTransactions!.Select(static tx => tx.Nonce), Is.EqualTo(new ulong[] { 1, 2 }));
            Assert.That(decoded.WithReplacedHeader(decoded.Header.Clone()).InclusionLists, Is.SameAs(decoded.InclusionLists));
        }
    }

    [Test]
    public void Compliance_digest_binds_list_split_and_verified_proof_deps()
    {
        Transaction a = new() { Hash = TestItem.KeccakA }, b = new() { Hash = TestItem.KeccakB };
        byte[] dependency = new byte[96], other = new byte[96];
        other[^1] = 1;
        static CommitteeInclusionList Proven(Transaction[] transactions, byte[] proof, params byte[][] proofDeps) =>
            new(transactions, proof, proofDeps) { VerifiedProofDeps = new HashSet<FrameDependency>() };
        static ValueHash256 Digest(params CommitteeInclusionList[] lists)
        {
            Block block = Build.A.Block.TestObject;
            block.InclusionLists = lists;
            block.InclusionListTransactions = CommitteeInclusionList.Flatten(lists);
            return NewPayloadHandler.ComputeInclusionListDigest(block);
        }
        ValueHash256 original = Digest(Proven([a], [1], dependency), new([b]));
        Block flattened = Build.A.Block.TestObject;
        flattened.InclusionListTransactions = [a, b];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(NewPayloadHandler.ComputeInclusionListDigest(flattened), Is.Not.EqualTo(original));
            Assert.That(Digest(Proven([a, b], [1], dependency)), Is.Not.EqualTo(original), "list split");
            Assert.That(Digest(new([a]), Proven([b], [1], dependency)), Is.Not.EqualTo(original), "which list is proven");
            Assert.That(Digest(Proven([a], [1], other), new([b])), Is.Not.EqualTo(original), "proof_deps");
            Assert.That(Digest(new([a], [1], [dependency]), new([b])), Is.Not.EqualTo(original), "an unverified proof");
            // EIPs#12475: obligations follow from a verified proof's dependencies, not from its bytes.
            Assert.That(Digest(Proven([a], [2], dependency), new([b])), Is.EqualTo(original), "proof bytes");
        }
    }

    [Test]
    public void Payload_id_commits_to_each_list()
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        static InclusionListV1 List(byte proof, byte dependency) =>
            new() { Transactions = [[0x01]], RecursiveStark = new() { StarkProof = [proof], ProofDeps = [Enumerable.Repeat(dependency, 96).ToArray()] } };
        string original = Attributes([List(1, 1)]).GetPayloadId(parent);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Attributes([List(2, 1)]).GetPayloadId(parent), Is.Not.EqualTo(original));
            Assert.That(Attributes([List(1, 2)]).GetPayloadId(parent), Is.Not.EqualTo(original));
            Assert.That(Attributes([List(1, 1), List(1, 1)]).GetPayloadId(parent), Is.Not.EqualTo(original));
            Assert.That(Attributes([new() { Transactions = [[0x01]], RecursiveStark = RecursiveStarkV1.Empty }]).GetPayloadId(parent), Is.Not.EqualTo(original));
            Assert.That(Attributes(null, [[0x01]]).GetPayloadId(parent), Is.Not.EqualTo(original));
        }
    }

    [Test]
    public void Combined_transactions_flatten_the_lists_once()
    {
        PayloadAttributes attributes = Attributes([new() { Transactions = [[1], [2]] }, new() { Transactions = [[3]] }]);
        byte[][] combined = attributes.CombinedInclusionListTransactions!;
        Assert.That(combined, Is.EqualTo(new byte[][] { [1], [2], [3] }));
        Assert.That(attributes.CombinedInclusionListTransactions, Is.SameAs(combined), "the array identifies the build");
    }

    private static byte[] Encode(Transaction transaction) => Nethermind.Serialization.Rlp.TxDecoder.Instance
        .Encode(transaction, Nethermind.Serialization.Rlp.RlpBehaviors.SkipTypedWrapping).Bytes;
}

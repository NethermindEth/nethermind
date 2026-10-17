// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        attributes.InclusionLists = [new() { CommitteeIndex = 99, Transactions = [] }];
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
    [TestCase("duplicate-position", true)]
    [TestCase("descending-position", true)]
    [TestCase("position-out-of-range", true)]
    [TestCase("oversized-list", true)]
    [TestCase("incomplete-commitment", true)]
    // EIPs#12475: an undecodable entry or a commitment to a proof this node does not hold is scoped to its list: the entry
    // contributes nothing and no proof is aggregated, while obligations follow from the commitment, so it must not fail the
    // request for every other list.
    [TestCase("empty-transaction", false)]
    [TestCase("unheld-proof", false)]
    [TestCase("commitment-for-empty-dependencies", false)]
    [TestCase("null-commitment", false)]
    public void List_shape_is_checked_at_each_entry_point(string invalidShape, bool rejected)
    {
        RecursiveStarkV1? proof = invalidShape switch
        {
            "null-commitment" => null,
            "incomplete-commitment" => new RecursiveStarkV1 { DepsHash = TestItem.KeccakB },
            _ => new RecursiveStarkV1 { StarkProofHash = TestItem.KeccakA, DepsHash = TestItem.KeccakB }
        };
        InclusionListV1[] lists = invalidShape switch
        {
            "missing-transactions" => [new() { Transactions = null }],
            "duplicate-position" => [new() { CommitteeIndex = 4, Transactions = [] }, new() { CommitteeIndex = 4, Transactions = [] }],
            "descending-position" => [new() { CommitteeIndex = 4, Transactions = [] }, new() { CommitteeIndex = 2, Transactions = [] }],
            "position-out-of-range" => [new() { CommitteeIndex = Eip7805Constants.InclusionListCommitteeSize, Transactions = [] }],
            "missing-transaction" => [new() { Transactions = [null!] }],
            "empty-transaction" => [new() { Transactions = [[]] }],
            "oversized-list" => [new() { Transactions = [new byte[Eip7805Constants.MaxBytesPerInclusionList + 1]] }],
            _ => [new() { Transactions = [], RecursiveStark = proof }]
        };
        string? listError = InclusionListV1.Validate(lists);
        Assert.That(listError is not null, Is.EqualTo(rejected));
        Assert.That(Attributes(lists).Validate(new TestSpecProvider(PerListSpec), 6, out _),
            Is.EqualTo(rejected ? PayloadAttributesValidationResult.InvalidPayloadAttributes : PayloadAttributesValidationResult.Success));

        // The test payload fails a later, unrelated check, so an accepted list must reach the same error as a well-formed one.
        ExecutionPayloadV3 payload = ExecutionPayloadV3.Create(Build.A.Block.TestObject);
        new ExecutionPayloadParams<ExecutionPayloadV3>(payload, [], Keccak.Zero, [], null, [new() { Transactions = [] }])
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
            new() { CommitteeIndex = 1, Transactions = [Encode(first), [0xc0]], RecursiveStark = new() { StarkProofHash = TestItem.KeccakA, DepsHash = TestItem.KeccakB } },
            new() { CommitteeIndex = 2, Transactions = [Encode(second)] }
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
            Assert.That(decoded.InclusionLists![0].CommitteeIndex, Is.EqualTo(1));
            Assert.That(decoded.InclusionLists[0].Transactions.Select(static tx => tx.Nonce), Is.EqualTo(new ulong[] { 1 }), "an undecodable entry is dropped");
            Assert.That(decoded.InclusionLists[0].Commitment, Is.EqualTo(new InclusionListCommitment(TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256)));
            Assert.That(decoded.InclusionLists[1].Commitment, Is.Null);
            Assert.That(decoded.InclusionListTransactions!.Select(static tx => tx.Nonce), Is.EqualTo(new ulong[] { 1, 2 }));
            Assert.That(decoded.WithReplacedHeader(decoded.Header.Clone()).InclusionLists, Is.SameAs(decoded.InclusionLists));
        }
    }

    [Test]
    public void Compliance_digest_binds_list_split_positions_and_commitments()
    {
        Transaction a = new() { Hash = TestItem.KeccakA }, b = new() { Hash = TestItem.KeccakB };
        InclusionListCommitment proof = new(TestItem.KeccakC.ValueHash256, TestItem.KeccakD.ValueHash256);
        static ValueHash256 Digest(params CommitteeInclusionList[] lists)
        {
            // As newPayload resolves them for a node holding every committed proof.
            foreach (CommitteeInclusionList list in lists) list.HoldsValidProof = list.IsCommitted;
            Block block = Build.A.Block.TestObject;
            block.InclusionLists = lists;
            block.InclusionListTransactions = CommitteeInclusionList.Flatten(lists);
            return NewPayloadHandler.ComputeInclusionListDigest(block);
        }
        ValueHash256 original = Digest(new(0, [a], proof), new(1, [b], null));
        Block flattened = Build.A.Block.TestObject;
        flattened.InclusionListTransactions = [a, b];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(NewPayloadHandler.ComputeInclusionListDigest(flattened), Is.Not.EqualTo(original));
            Assert.That(Digest(new CommitteeInclusionList(0, [a, b], proof)), Is.Not.EqualTo(original), "list split");
            Assert.That(Digest(new(5, [a], proof), new(1, [b], null)), Is.Not.EqualTo(original), "committee position");
            Assert.That(Digest(new(0, [a], null), new(1, [b], proof)), Is.Not.EqualTo(original), "which list is committed");
            // EIPs#12475: obligations follow from whether a list's commitment is backed by a held proof, not from which proof it names.
            Assert.That(Digest(new(0, [a], new InclusionListCommitment(TestItem.KeccakE.ValueHash256, default)), new(1, [b], null)),
                Is.EqualTo(original), "committed proof");
        }
    }

    [Test]
    public void Payload_id_commits_to_each_list()
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        static InclusionListV1 List(ulong index, byte proof) =>
            new() { CommitteeIndex = index, Transactions = [[0x01]], RecursiveStark = new() { StarkProofHash = Keccak.Compute([proof]), DepsHash = TestItem.KeccakB } };
        string original = Attributes([List(0, 1)]).GetPayloadId(parent);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Attributes([List(1, 1)]).GetPayloadId(parent), Is.Not.EqualTo(original));
            Assert.That(Attributes([List(0, 2)]).GetPayloadId(parent), Is.Not.EqualTo(original));
            Assert.That(Attributes([new() { Transactions = [[0x01]] }]).GetPayloadId(parent), Is.Not.EqualTo(original));
            Assert.That(Attributes(null, [[0x01]]).GetPayloadId(parent), Is.Not.EqualTo(original));
        }
    }

    [Test]
    public void Combined_transactions_flatten_the_lists_once()
    {
        PayloadAttributes attributes = Attributes([new() { CommitteeIndex = 3, Transactions = [[1], [2]] }, new() { CommitteeIndex = 0, Transactions = [[3]] }]);
        byte[][] combined = attributes.CombinedInclusionListTransactions!;
        Assert.That(combined, Is.EqualTo(new byte[][] { [1], [2], [3] }));
        Assert.That(attributes.CombinedInclusionListTransactions, Is.SameAs(combined), "the array identifies the build");
    }

    private static byte[] Encode(Transaction transaction) => Nethermind.Serialization.Rlp.TxDecoder.Instance
        .Encode(transaction, Nethermind.Serialization.Rlp.RlpBehaviors.SkipTypedWrapping).Bytes;
}

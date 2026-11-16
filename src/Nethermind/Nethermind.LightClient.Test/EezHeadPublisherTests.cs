// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;

namespace Nethermind.LightClient.Test;

public class EezHeadPublisherTests
{
    private static readonly Address Registry = new("0x4b2760f84b83d6b3829e30a7573b0172b6d6402c");
    private static readonly EezSettlement Settlement = new(Registry, 2, 696_991);

    [Test]
    public async Task Newer_commitment_is_published_with_rollup_timestamp()
    {
        Chain chain = new();
        BlockHeader header = chain.Commit(10);

        Assert.That(await chain.Publisher.UpdateAsync(L1(100), CancellationToken.None), Is.True);

        Assert.That(chain.Published, Has.Count.EqualTo(1));
        Assert.That(chain.Published[0].Head.BlockHash, Is.EqualTo(header.Hash));
        Assert.That(chain.Published[0].Head.Slot, Is.EqualTo(L1(100).Slot));
        Assert.That(chain.Published[0].Timestamp, Is.EqualTo(header.Timestamp));
        Assert.That(chain.Publisher.Current!.SettlementHead, Is.EqualTo(L1(100)));
    }

    [Test]
    public async Task Same_or_older_settlement_head_is_not_read_again()
    {
        Chain chain = new();
        chain.Commit(10);
        await chain.Publisher.UpdateAsync(L1(100), CancellationToken.None);
        int reads = chain.Settlement.StorageReads;

        Assert.That(await chain.Publisher.UpdateAsync(L1(100), CancellationToken.None), Is.False);
        Assert.That(await chain.Publisher.UpdateAsync(L1(99), CancellationToken.None), Is.False);
        Assert.That(chain.Settlement.StorageReads, Is.EqualTo(reads));
        Assert.That(chain.Published, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Unchanged_commitment_refreshes_settlement_provenance()
    {
        Chain chain = new();
        chain.Commit(10);
        await chain.Publisher.UpdateAsync(L1(100), CancellationToken.None);

        Assert.That(await chain.Publisher.UpdateAsync(L1(101), CancellationToken.None), Is.True);

        Assert.That(chain.Published, Has.Count.EqualTo(2));
        Assert.That(chain.Published[1].Head.Number, Is.EqualTo(10));
        Assert.That(chain.Published[1].Head.Slot, Is.EqualTo(L1(101).Slot));
    }

    [Test]
    public async Task Rollback_to_older_rollup_block_is_rejected_and_head_kept()
    {
        Chain chain = new();
        BlockHeader older = chain.Commit(5);
        chain.Commit(10);
        await chain.Publisher.UpdateAsync(L1(100), CancellationToken.None);
        EezCommittedHead current = chain.Publisher.Current!;
        chain.Point(older);

        Assert.That(() => chain.Publisher.UpdateAsync(L1(101), CancellationToken.None), Throws.InstanceOf<InvalidDataException>());
        Assert.That(chain.Publisher.Current, Is.SameAs(current));
        Assert.That(chain.Published, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Conflicting_block_at_same_height_is_rejected()
    {
        Chain chain = new();
        chain.Commit(10);
        await chain.Publisher.UpdateAsync(L1(100), CancellationToken.None);
        chain.Commit(10, "fork");

        Assert.That(() => chain.Publisher.UpdateAsync(L1(101), CancellationToken.None), Throws.InstanceOf<InvalidDataException>());
        Assert.That(chain.Published, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Missing_commitment_keeps_last_head()
    {
        Chain chain = new();
        chain.Commit(10);
        await chain.Publisher.UpdateAsync(L1(100), CancellationToken.None);
        EezCommittedHead current = chain.Publisher.Current!;
        chain.Settlement.Value = UInt256.Zero;

        Assert.That(await chain.Publisher.UpdateAsync(L1(101), CancellationToken.None), Is.False);
        Assert.That(chain.Publisher.Current, Is.SameAs(current));
    }

    [Test]
    public async Task Unavailable_rollup_header_keeps_last_head_and_retries()
    {
        Chain chain = new();
        chain.Commit(10);
        await chain.Publisher.UpdateAsync(L1(100), CancellationToken.None);
        BlockHeader next = chain.Commit(16);
        chain.Rollup.Headers.Remove(next.Hash!);

        Assert.That(() => chain.Publisher.UpdateAsync(L1(101), CancellationToken.None),
            Throws.InstanceOf<ExecutionDataUnavailableException>());
        Assert.That(chain.Publisher.Current!.Head.Number, Is.EqualTo(10));

        chain.Rollup.Headers[next.Hash!] = next;
        Assert.That(await chain.Publisher.UpdateAsync(L1(101), CancellationToken.None), Is.True);
        Assert.That(chain.Publisher.Current!.Head.Number, Is.EqualTo(16));
    }

    private static VerifiedHead L1(ulong number) =>
        new(number + 1_000, number, Keccak.Compute($"l1 block {number}"), Keccak.Compute($"l1 state {number}"));

    private sealed class Chain
    {
        internal readonly SettlementSource Settlement = new();
        internal readonly RollupSource Rollup = new();
        internal readonly List<(VerifiedHead Head, ulong Timestamp)> Published = [];
        internal readonly EezHeadPublisher Publisher;

        internal Chain() => Publisher = new(Settlement, Rollup, EezHeadPublisherTests.Settlement,
            (head, timestamp) => Published.Add((head, timestamp)));

        internal BlockHeader Commit(ulong number, string salt = "")
        {
            BlockHeader header = new(Keccak.Compute($"parent {number}{salt}"), Keccak.OfAnEmptySequenceRlp, Address.Zero,
                UInt256.Zero, number, 30_000_000, 1_800_000_000 + number * 2, [])
            {
                StateRoot = Keccak.Compute($"state {number}{salt}"),
                TxRoot = Keccak.EmptyTreeHash,
                ReceiptsRoot = Keccak.EmptyTreeHash,
                Bloom = Bloom.Empty,
                BaseFeePerGas = 7,
            };
            header.Hash = header.CalculateHash();
            Rollup.Headers[header.Hash] = header;
            Point(header);
            return header;
        }

        internal void Point(BlockHeader header) => Settlement.Value = new UInt256(header.Hash!.Bytes, isBigEndian: true);
    }

    private sealed class SettlementSource : IExecutionStateSource
    {
        internal UInt256 Value;
        internal int StorageReads;

        public Task<Account> GetAccountAsync(VerifiedHead head, Address address, CancellationToken cancellationToken) =>
            Task.FromResult(new Account(1, 0, Keccak.Compute("registry storage"), Keccak.Compute("registry code")));

        public Task<UInt256> GetStorageAsync(VerifiedHead head, Address address, Account account, UInt256 key, CancellationToken cancellationToken)
        {
            StorageReads++;
            return Task.FromResult(key == EezCommitment.RootSlot(2) ? Value : UInt256.Zero);
        }

        public Task<byte[]> GetCodeAsync(Account account, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BlockHeader> GetHeaderAsync(VerifiedHead head, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Hash256[]> GetAncestorHashesAsync(VerifiedHead head, ulong firstNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RollupSource : IExecutionStateSource
    {
        internal readonly Dictionary<Hash256, BlockHeader> Headers = [];

        public Task<BlockHeader> GetHeaderByHashAsync(Hash256 hash, CancellationToken cancellationToken) =>
            Headers.TryGetValue(hash, out BlockHeader? header)
                ? Task.FromResult(header)
                : Task.FromException<BlockHeader>(new ExecutionDataUnavailableException("Execution peer does not have the requested header."));

        public Task<Account> GetAccountAsync(VerifiedHead head, Address address, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UInt256> GetStorageAsync(VerifiedHead head, Address address, Account account, UInt256 key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<byte[]> GetCodeAsync(Account account, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BlockHeader> GetHeaderAsync(VerifiedHead head, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Hash256[]> GetAncestorHashesAsync(VerifiedHead head, ulong firstNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

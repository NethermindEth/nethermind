// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;

namespace Nethermind.LightClient.Test;

public class EezCommitmentTests
{
    private static readonly Address Registry = new("0x4b2760f84b83d6b3829e30a7573b0172b6d6402c");
    private static readonly EezSettlement Settlement = new(Registry, 2, 696_991);
    private static readonly VerifiedHead SettlementHead = new(1_000, 23_000_000, Keccak.Compute("l1 block"), Keccak.Compute("l1 state"));

    [Test]
    public void Root_slot_matches_mapping_layout()
    {
        Assert.That(EezCommitment.RootSlot(2),
            Is.EqualTo(UInt256.Parse("0x679795a0195a1b76cdebb7c51d74e058aee92919b8c3389af86ef24535e8a28d")));
    }

    [Test]
    public async Task Committed_header_becomes_rollup_head()
    {
        BlockHeader header = RollupHeader(1_234);
        Source settlement = Source.WithCommitment(header.Hash!);
        Source rollup = new() { Header = header };

        EezCommittedHead? result = await EezCommitment.ResolveAsync(settlement, rollup, Settlement, SettlementHead, CancellationToken.None);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Head, Is.EqualTo(new VerifiedHead(SettlementHead.Slot, 1_234, header.Hash!, header.StateRoot!)));
        Assert.That(result.SettlementHead, Is.SameAs(SettlementHead));
        Assert.That(settlement.RequestedHeads, Is.All.SameAs(SettlementHead));
        Assert.That(settlement.RequestedKeys, Is.EqualTo(new[] { EezCommitment.RootSlot(2) }));
    }

    [Test]
    public async Task Registry_without_storage_has_no_commitment()
    {
        Source settlement = new() { Account = Account.TotallyEmpty };
        Source rollup = new() { Header = RollupHeader(1) };

        Assert.That(await EezCommitment.ResolveAsync(settlement, rollup, Settlement, SettlementHead, CancellationToken.None), Is.Null);
        Assert.That(settlement.RequestedKeys, Is.Empty);
        Assert.That(rollup.HeaderRequests, Is.Zero);
    }

    [Test]
    public async Task Zero_commitment_is_not_resolved()
    {
        Source settlement = new();
        Source rollup = new() { Header = RollupHeader(1) };

        Assert.That(await EezCommitment.ResolveAsync(settlement, rollup, Settlement, SettlementHead, CancellationToken.None), Is.Null);
        Assert.That(rollup.HeaderRequests, Is.Zero);
    }

    [Test]
    public async Task Commitment_for_other_rollup_is_not_read()
    {
        BlockHeader header = RollupHeader(5);
        Source settlement = Source.WithCommitment(header.Hash!, EezCommitment.RootSlot(3));
        Source rollup = new() { Header = header };

        Assert.That(await EezCommitment.ResolveAsync(settlement, rollup, Settlement, SettlementHead, CancellationToken.None), Is.Null);
        Assert.That(rollup.HeaderRequests, Is.Zero);
    }

    [Test]
    public void Header_with_tampered_state_root_is_rejected()
    {
        BlockHeader header = RollupHeader(7);
        Hash256 committed = header.Hash!;
        header.StateRoot = Keccak.Compute("forged state");
        Source settlement = Source.WithCommitment(committed);
        Source rollup = new() { Header = header };

        Assert.That(() => EezCommitment.ResolveAsync(settlement, rollup, Settlement, SettlementHead, CancellationToken.None),
            Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void Header_for_other_hash_is_rejected()
    {
        BlockHeader header = RollupHeader(7);
        Source settlement = Source.WithCommitment(Keccak.Compute("other header"));
        Source rollup = new() { Header = header };

        Assert.That(() => EezCommitment.ResolveAsync(settlement, rollup, Settlement, SettlementHead, CancellationToken.None),
            Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void Unavailable_rollup_header_is_reported()
    {
        BlockHeader header = RollupHeader(7);
        Source settlement = Source.WithCommitment(header.Hash!);
        Source rollup = new();

        Assert.That(() => EezCommitment.ResolveAsync(settlement, rollup, Settlement, SettlementHead, CancellationToken.None),
            Throws.InstanceOf<ExecutionDataUnavailableException>());
    }

    private static BlockHeader RollupHeader(ulong number)
    {
        BlockHeader header = new(Keccak.Compute($"parent {number}"), Keccak.OfAnEmptySequenceRlp, Address.Zero, UInt256.Zero,
            number, 30_000_000, 1_800_000_000 + number * 2, [])
        {
            StateRoot = Keccak.Compute($"state {number}"),
            TxRoot = Keccak.EmptyTreeHash,
            ReceiptsRoot = Keccak.EmptyTreeHash,
            Bloom = Bloom.Empty,
            BaseFeePerGas = 7,
        };
        header.Hash = header.CalculateHash();
        return header;
    }

    private sealed class Source : IExecutionStateSource
    {
        internal Account Account = new(1, 0, Keccak.Compute("registry storage"), Keccak.Compute("registry code"));
        internal Dictionary<UInt256, UInt256> Storage = [];
        internal List<VerifiedHead> RequestedHeads = [];
        internal List<UInt256> RequestedKeys = [];
        internal BlockHeader? Header;
        internal int HeaderRequests;

        internal static Source WithCommitment(Hash256 hash) => WithCommitment(hash, EezCommitment.RootSlot(2));

        internal static Source WithCommitment(Hash256 hash, UInt256 slot)
        {
            Source source = new();
            source.Storage[slot] = new UInt256(hash.Bytes, isBigEndian: true);
            return source;
        }

        public Task<Account> GetAccountAsync(VerifiedHead head, Address address, CancellationToken cancellationToken)
        {
            RequestedHeads.Add(head);
            return Task.FromResult(address == Registry ? Account : Account.TotallyEmpty);
        }

        public Task<UInt256> GetStorageAsync(VerifiedHead head, Address address, Account account, UInt256 key, CancellationToken cancellationToken)
        {
            RequestedHeads.Add(head);
            RequestedKeys.Add(key);
            return Task.FromResult(Storage.GetValueOrDefault(key));
        }

        public Task<BlockHeader> GetHeaderByHashAsync(Hash256 hash, CancellationToken cancellationToken)
        {
            HeaderRequests++;
            return Header is null
                ? Task.FromException<BlockHeader>(new ExecutionDataUnavailableException("Execution peer does not have the requested header."))
                : Task.FromResult(Header);
        }

        public Task<byte[]> GetCodeAsync(Account account, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BlockHeader> GetHeaderAsync(VerifiedHead head, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Hash256[]> GetAncestorHashesAsync(VerifiedHead head, ulong firstNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

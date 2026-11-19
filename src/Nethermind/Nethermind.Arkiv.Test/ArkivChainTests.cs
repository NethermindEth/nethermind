// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nethermind.Api;
using Nethermind.Arkiv.Native;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Arkiv.Test;

/// <summary>
/// <see cref="ArkivChain"/> on real Rabdi blocks, over a GolemDB store on disk: the state roots and receipts Arkiv's
/// executor reaches, and the store kept in step with Nethermind's chain across restarts, a store left behind, and reorgs.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ArkivChainTests
{
    private string _directory = null!;
    private MemDb _db = null!;
    private Dictionary<Hash256, Block> _tree = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "arkiv-chain-tests-" + Guid.NewGuid().ToString("N"));
        _db = new MemDb();
        _tree = [];
        foreach (Block block in RabdiFixture.Blocks) _tree[block.Hash!] = block;
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private ArkivChain Open(ulong checkpointInterval = 0) =>
        RabdiFixture.OpenChain(_directory, _db, RabdiFixture.BlockTreeOver(_tree), checkpointInterval);

    private static Block B(int number) => RabdiFixture.Blocks[number - 1];

    [Test]
    public void Opens_at_the_networks_genesis()
    {
        using ArkivChain chain = Open();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.GenesisHash, Is.EqualTo(RabdiFixture.GenesisHash));
            Assert.That(chain.GenesisRoot, Is.EqualTo(RabdiFixture.GenesisRoot));
            Assert.That(chain.Head, Is.EqualTo((0UL, RabdiFixture.GenesisHash)));
        }
    }

    [Test]
    public void Every_block_reaches_its_headers_state_root_receipts_root_and_gas()
    {
        using ArkivChain chain = Open();
        foreach (Block block in RabdiFixture.Blocks)
        {
            ArkivBlockResult result = chain.Execute(block, commit: true);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.StateRoot, Is.EqualTo(block.StateRoot!), $"block {block.Number}");
                Assert.That(result.GasUsed, Is.EqualTo(block.GasUsed), $"block {block.Number}");
                Assert.That(result.RequestsEmpty, Is.True);
                Assert.That(ReceiptsRoot(block, result), Is.EqualTo(block.ReceiptsRoot!), $"block {block.Number}");
            }
        }
        Assert.That(chain.Head, Is.EqualTo((700UL, B(700).Hash!)));
    }

    [Test]
    public void Entity_transactions_log_and_touch_entities()
    {
        using ArkivChain chain = Open();
        int first = RabdiFixture.EntityBlockIndexes.First();
        for (int i = 0; i < first; i++) chain.Execute(RabdiFixture.Blocks[i], commit: true);
        ArkivBlockResult result = chain.Execute(RabdiFixture.Blocks[first], commit: true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Receipts.Skip(1).SelectMany(r => r.Logs).Any(), Is.True);
            Assert.That(result.Receipts.SelectMany(r => r.Logs).All(l => l.Address == RabdiFixture.ArkivAddress), Is.True);
            Assert.That(result.Entities, Is.Not.Empty);
        }
    }

    [Test]
    public void A_wrong_state_root_is_an_invalid_block_and_commits_nothing()
    {
        using ArkivChain chain = Open();
        Block tampered = B(1).WithReplacedHeader(B(1).Header.Clone());
        tampered.Header.StateRoot = Keccak.Compute("not the root");
        tampered.Header.Hash = tampered.Header.CalculateHash();

        InvalidBlockException? refused = Assert.Throws<InvalidBlockException>(() => chain.Execute(tampered, commit: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused!.Message, Does.Contain("state root mismatch"));
            Assert.That(chain.Head.Number, Is.EqualTo(0));
            Assert.That(chain.Execute(B(1), commit: true).StateRoot, Is.EqualTo(B(1).StateRoot!));
        }
    }

    [Test]
    public void A_block_with_withdrawals_is_refused_as_arkiv_reth_refuses_it()
    {
        using ArkivChain chain = Open();
        Block withdrawing = new(B(1).Header.Clone(), new BlockBody(B(1).Transactions, [], [new Withdrawal { Index = 0, ValidatorIndex = 1, Address = RabdiFixture.PurgeCaller, AmountInGwei = 1 }]));

        InvalidBlockException? refused = Assert.Throws<InvalidBlockException>(() => chain.Execute(withdrawing, commit: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused!.Message, Does.Contain("withdrawals"));
            Assert.That(chain.Head.Number, Is.EqualTo(0UL));
        }
    }

    [Test]
    public void A_read_only_run_leaves_the_store_where_it_was()
    {
        using ArkivChain chain = Open();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.Execute(B(1), commit: false).StateRoot, Is.EqualTo(B(1).StateRoot!));
            Assert.That(chain.Head.Number, Is.EqualTo(0));
        }
        chain.Execute(B(1), commit: true);
        Assert.That(chain.Head.Number, Is.EqualTo(1));
    }

    [Test]
    public void After_a_restart_committed_blocks_replay_and_the_chain_continues()
    {
        using (ArkivChain chain = Open())
        {
            for (int n = 1; n <= 300; n++) chain.Execute(B(n), commit: true);
        }

        using ArkivChain reopened = Open();
        Assert.That(reopened.Head, Is.EqualTo((300UL, B(300).Hash!)));
        // Nethermind lost its last blocks and processes them again: answered from what was committed, not re-run.
        ArkivBlockResult replayed = reopened.Execute(B(150), commit: true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(replayed.StateRoot, Is.EqualTo(B(150).StateRoot!));
            Assert.That(reopened.Head.Number, Is.EqualTo(300));
            Assert.That(reopened.Execute(B(301), commit: true).StateRoot, Is.EqualTo(B(301).StateRoot!));
        }
    }

    [Test]
    public void A_store_behind_the_chain_is_brought_forward_from_the_block_tree()
    {
        using ArkivChain chain = Open();
        for (int n = 1; n <= 100; n++) chain.Execute(B(n), commit: true);

        // Nethermind is at 199 (say, the store directory was restored from an older copy); block 200 arrives.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.Execute(B(200), commit: true).StateRoot, Is.EqualTo(B(200).StateRoot!));
            Assert.That(chain.Head, Is.EqualTo((200UL, B(200).Hash!)));
        }
    }

    [Test]
    public void A_reorg_below_the_head_restores_a_store_copy_and_replays()
    {
        using ArkivChain chain = Open(checkpointInterval: 50);
        for (int n = 1; n <= 120; n++) chain.Execute(B(n), commit: true);

        // 101' forks off 100: same contents, another hash.
        Block fork101 = RabdiFixture.Sibling(B(101));
        _tree[fork101.Hash!] = fork101;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.Execute(fork101, commit: true).StateRoot, Is.EqualTo(B(101).StateRoot!));
            Assert.That(chain.Head, Is.EqualTo((101UL, fork101.Hash!)));
        }

        // And back: the original chain's 121 builds on 120, through the original 101.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.Execute(B(121), commit: true).StateRoot, Is.EqualTo(B(121).StateRoot!));
            Assert.That(chain.Head, Is.EqualTo((121UL, B(121).Hash!)));
        }
    }

    [Test]
    public void A_reorg_with_no_store_copy_on_the_new_branch_is_refused()
    {
        using ArkivChain chain = Open();
        for (int n = 1; n <= 20; n++) chain.Execute(B(n), commit: true);
        Block fork11 = RabdiFixture.Sibling(B(11));
        _tree[fork11.Hash!] = fork11;

        InvalidOperationException? refused = Assert.Throws<InvalidOperationException>(() => chain.Execute(fork11, commit: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused!.Message, Does.Contain("no store copy"));
            Assert.That(chain.Head.Number, Is.EqualTo(20));
        }
    }

    [Test]
    public void Entity_history_answers_at_every_past_block()
    {
        using ArkivChain chain = Open();
        Dictionary<Hash256, ulong> touchedAt = [];
        foreach (Block block in RabdiFixture.Blocks)
        {
            ArkivBlockResult result = chain.Execute(block, commit: true);
            foreach (ArkivEntitySnapshot snapshot in result.Entities) touchedAt.TryAdd(snapshot.Key, block.Number);
        }
        Assert.That(touchedAt, Is.Not.Empty);

        (Hash256 key, ulong created) = touchedAt.First();
        Assert.That(chain.EntityAt(key, created - 1).Found, Is.False, "nothing touched it before it was created");
        (bool found, JsonElement? entity) = chain.EntityAt(key, created);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(entity!.Value.GetProperty("key").GetString(), Is.EqualTo(key.ToString()).IgnoreCase);
        }

        // At the head, the archive and GolemDB agree.
        JsonElement live = chain.Rpc("arkiv_debugEntityExists", $"[\"{key}\"]");
        (bool atHead, JsonElement? archived) = chain.EntityAt(key, 700);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(atHead, Is.True);
            Assert.That(live.GetProperty("result").GetBoolean(), Is.EqualTo(archived is not null));
        }
    }

    [Test]
    public void Rpc_refuses_past_blocks_as_the_reference_does()
    {
        using ArkivChain chain = Open();
        for (int n = 1; n <= 10; n++) chain.Execute(B(n), commit: true);
        JsonElement past = chain.Rpc("arkiv_getEntityCount", "[{\"block\":5}]");
        Assert.That(past.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32006));
        JsonElement head = chain.Rpc("arkiv_getEntityCount", "[]");
        Assert.That(head.GetProperty("result").ValueKind, Is.EqualTo(JsonValueKind.Number));
    }

    [Test]
    public void Accounts_read_back_from_the_store()
    {
        // Block 409 carries Rabdi's first plain transfer, 100 ETH.
        Block block = B(409);
        Transaction transfer = block.Transactions[1];
        using ArkivChain chain = Open();
        for (int n = 1; n <= 409; n++) chain.Execute(B(n), commit: true);

        (bool exists, _, ulong nonce) = chain.Account(transfer.SenderAddress!);
        (bool funded, UInt256 balance, _) = chain.Account(transfer.To!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exists, Is.True);
            Assert.That(nonce, Is.EqualTo(transfer.Nonce + 1));
            Assert.That(funded, Is.True);
            Assert.That(balance, Is.GreaterThanOrEqualTo(transfer.Value));

            // The block reports both, with the store's values: they are what the account trie mirrors.
            Assert.That(chain.TryGetResult(block.Hash!, out ArkivBlockResult result), Is.True);
            Assert.That(result.Accounts, Does.Contain(new ArkivAccount(transfer.SenderAddress!, chain.Account(transfer.SenderAddress!).Balance, nonce)));
            Assert.That(result.Accounts, Does.Contain(new ArkivAccount(transfer.To!, balance, chain.Account(transfer.To!).Nonce)));
        }
    }

    [Test]
    public void The_protocol_purge_is_not_an_account_transaction()
    {
        // arkiv_transact runs purgeExpired with no sender checks and no charge: its nonce is the block number, and its
        // sender never becomes an account.
        using ArkivChain chain = Open();
        for (int n = 1; n <= 5; n++) chain.Execute(B(n), commit: true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(B(5).Transactions[0].Nonce, Is.EqualTo(5UL));
            Assert.That(chain.Account(RabdiFixture.PurgeCaller).Exists, Is.False);
        }
    }

    private static Hash256 ReceiptsRoot(Block block, ArkivBlockResult result)
    {
        TxReceipt[] receipts = block.Transactions.Select((tx, i) => new TxReceipt
        {
            TxType = tx.Type,
            StatusCode = result.Receipts[i].Success ? StatusCode.Success : StatusCode.Failure,
            GasUsedTotal = result.Receipts[i].CumulativeGasUsed,
            Logs = result.Receipts[i].Logs,
            Bloom = new Bloom(result.Receipts[i].Logs),
        }).ToArray();
        return ReceiptsRootCalculator.Instance.GetReceiptsRoot(receipts, Prague.Instance, block.ReceiptsRoot);
    }
}

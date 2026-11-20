//! The shim against the live chain's published facts: Rabdi's genesis block, as its public RPC reports it.

use super::*;

const RABDI_GENESIS: &str = include_str!("../../../Nethermind/Chains/rabdi.json");

/// `eth_getBlockByNumber(0)` on https://rpc.rabdi.db-chain.testnet.arkiv.network.
const RABDI_GENESIS_HASH: &str = "0xa65a2b9e25129f0e8ccdf494dfd66ec744bdf400c806b832b1da86b0b939cbce";
const RABDI_GENESIS_ROOT: &str = "0x18f6f20576ca53378a20d9bf891eae550cdfdbc1473b05fe104e48f61720a9ec";

#[test]
fn rabdi_genesis_is_the_live_chains() {
    let node = Node::open(RABDI_GENESIS, None).unwrap();
    assert_eq!(format!("{}", node.genesis_root), RABDI_GENESIS_ROOT);
    assert_eq!(format!("{}", node.genesis_hash), RABDI_GENESIS_HASH);
    let (commit, tag, root) = node.head().unwrap();
    assert_eq!(commit, CommitId::GENESIS.0 + 1, "genesis is the first commit");
    assert_eq!(tag, node.genesis_hash);
    assert_eq!(root, node.genesis_root);
}

#[test]
fn a_store_reopens_at_its_head() {
    let dir = std::env::temp_dir().join(format!("arkiv-ffi-reopen-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    let first = Node::open(RABDI_GENESIS, Some(&dir)).unwrap();
    let head = first.head().unwrap();
    drop(first);
    let again = Node::open(RABDI_GENESIS, Some(&dir)).unwrap();
    assert_eq!(again.head().unwrap(), head);
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn a_block_off_the_head_is_refused() {
    let node = Node::open(RABDI_GENESIS, None).unwrap();
    let mut block = Block::default();
    block.header.number = 1;
    block.header.parent_hash = B256::repeat_byte(7);
    let (status, _) = node.execute_block(block, vec![], Apply::Commit).unwrap_err();
    assert_eq!(status, Status::NotOnHead);
}

#[test]
fn genesis_accounts_are_readable() {
    let node = Node::open(RABDI_GENESIS, None).unwrap();
    let genesis: serde_json::Value = serde_json::from_str(RABDI_GENESIS).unwrap();
    let (address, account) = genesis["alloc"].as_object().unwrap().iter().find(|(_, a)| a["balance"] != "0x0").unwrap();
    let address: Address = address.parse().unwrap();
    let expected: U256 = account["balance"].as_str().unwrap().parse().unwrap();
    assert_eq!(node.account(address).unwrap().map(|a| a.0), Some(expected));
}

/// Rabdi blocks 1-700 with their senders, from the plugin tests' fixture (raw RLP, gzipped).
fn rabdi_blocks() -> Vec<(Block, Vec<Address>)> {
    use alloy_consensus::transaction::SignerRecoverable;
    let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../../Nethermind/Nethermind.Arkiv.Test/TestData/rabdi-blocks-1-700.rlp.hex.gz");
    let out = std::process::Command::new("gzip").args(["-dc", path]).output().expect("gzip");
    String::from_utf8(out.stdout)
        .unwrap()
        .lines()
        .map(|line| {
            let bytes = alloy_primitives::hex::decode(line).unwrap();
            let block = Block::decode(&mut &bytes[..]).unwrap();
            let senders = block.body.transactions.iter().map(|tx| tx.recover_signer().unwrap()).collect();
            (block, senders)
        })
        .collect()
}

/// Every account a block writes is reported, with GolemDB's values after the block: the account trie the host keeps
/// for historical eth_getBalance / eth_getTransactionCount is built from these.
#[test]
fn a_block_reports_the_accounts_it_wrote() {
    let node = Node::open(RABDI_GENESIS, None).unwrap();
    let mut transfers = 0;
    for (block, senders) in rabdi_blocks() {
        let user_txs = block.body.transactions.len() > 1;
        let result = node.execute_block(block, senders.clone(), Apply::Commit).unwrap();
        if user_txs {
            assert!(!result.accounts.is_empty(), "a block with a user transaction reported no account");
            for sender in &senders[1..] {
                let reported = result.accounts.iter().find(|a| a.address == *sender).expect("each user sender is reported");
                assert_eq!(Some((reported.balance, reported.nonce)), node.account(*sender).unwrap());
            }
            transfers += 1;
        }
    }
    assert!(transfers >= 20, "{transfers} blocks with user transactions");
}

/// Building Rabdi's own blocks reproduces them: for every block with a non-empty purge in 1-700, the keys chosen for
/// it are the ones arkiv-reth's builder chose, and applying its transactions one by one through a build session seals
/// the root its header carries — without committing, so the block then imports as usual.
#[test]
fn building_rabdi_blocks_reproduces_their_purge_and_root() {
    use alloy_consensus::Transaction as _;
    use alloy_sol_types::SolCall;
    let node = Node::open(RABDI_GENESIS, None).unwrap();
    let mut checked = 0;
    for (block, senders) in rabdi_blocks() {
        let purge = &block.body.transactions[0];
        let named = arkiv_bindings::protocol::purgeExpiredCall::abi_decode(purge.input()).unwrap().entityKeys;
        if !named.is_empty() {
            assert_eq!(node.purge_keys(block.header.number).unwrap(), named, "purge keys of block {}", block.header.number);

            let session = node.build_begin(block.header.clone()).unwrap();
            for (tx, sender) in block.body.transactions.iter().zip(&senders) {
                session.apply(tx.clone(), *sender).unwrap();
            }
            let built = session.finish().unwrap();
            assert_eq!(built.state_root, block.header.state_root, "built root of block {}", block.header.number);
            assert_eq!(built.gas_used, block.header.gas_used);
            assert_eq!(node.head().unwrap().1, block.header.parent_hash, "a build commits nothing");
            checked += 1;
        }
        node.execute_block(block, senders, Apply::Commit).unwrap();
    }
    assert!(checked >= 3, "{checked} blocks with a non-empty purge");
}

/// A transaction Arkiv's executor refuses is skipped and leaves the build as it was.
#[test]
fn a_refused_transaction_leaves_the_build_untouched() {
    let node = Node::open(RABDI_GENESIS, None).unwrap();
    let blocks = rabdi_blocks();
    let (block, senders) = &blocks[0];
    let session = node.build_begin(block.header.clone()).unwrap();
    // The block's purge from any other sender is refused ("purgeExpired is protocol-only").
    let refused = session.apply(block.body.transactions[0].clone(), Address::repeat_byte(0x11));
    assert!(refused.unwrap_err().contains("protocol-only"));
    session.apply(block.body.transactions[0].clone(), senders[0]).unwrap();
    assert_eq!(session.finish().unwrap().state_root, block.header.state_root);
}

/// A store's accounts are listed from its committed state: after Rabdi's blocks 1-700 on disk, every genesis account
/// and every transaction sender that holds an account is listed, and nothing that is not an account.
#[test]
fn a_stores_accounts_are_all_listed() {
    let dir = std::env::temp_dir().join(format!("arkiv-accounts-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    let mut senders = std::collections::BTreeSet::new();
    {
        let node = Node::open(RABDI_GENESIS, Some(&dir)).unwrap();
        for (block, block_senders) in rabdi_blocks() {
            senders.extend(block_senders.iter().copied());
            node.execute_block(block, block_senders, Apply::Commit).unwrap();
        }
    }
    let listed: std::collections::BTreeSet<Address> = store::account_addresses(&dir).unwrap().into_iter().collect();
    let node = Node::open(RABDI_GENESIS, Some(&dir)).unwrap();
    let spec = ArkivChainSpecParser::parse(RABDI_GENESIS).unwrap();
    for address in spec.genesis().alloc.keys() {
        assert!(listed.contains(address), "genesis account {address} is listed");
    }
    // The protocol purge's sender holds no account: Arkiv skips its nonce and fees and writes nothing for it.
    let purge_sender: Address = "0x5ff20d1c9fea02c505a2c18f6d42feaeaf44c0af".parse().unwrap();
    assert_eq!(node.account(purge_sender).unwrap(), None);
    for sender in senders.iter().filter(|s| **s != purge_sender) {
        assert!(listed.contains(sender), "sender {sender} is listed");
    }
    // Every listed address is an account the store holds.
    for address in &listed {
        assert!(node.account(*address).unwrap().is_some(), "{address} is listed but holds no account");
    }
    let _ = std::fs::remove_dir_all(&dir);
}

/// Builds of one block run side by side (as overlapping payload builds do): finishing one leaves the others' sealed
/// branches alone, and each reaches the root the header carries.
#[test]
fn concurrent_builds_of_one_block_both_finish() {
    let node = Node::open(RABDI_GENESIS, None).unwrap();
    let blocks = rabdi_blocks();
    let (target, senders) = blocks.iter().find(|(block, _)| block.body.transactions.len() > 1).unwrap().clone();
    for (block, block_senders) in blocks.iter().take_while(|(block, _)| block.header.number < target.header.number) {
        node.execute_block(block.clone(), block_senders.clone(), Apply::Commit).unwrap();
    }

    // Finished at the same moment, many times over: with one registry shared, a session finishing could drop the
    // other's seal between that one sealing and reading its root.
    for _ in 0..50 {
        let sessions: Vec<_> = (0..4).map(|_| node.build_begin(target.header.clone()).unwrap()).collect();
        for session in &sessions {
            for (tx, sender) in target.body.transactions.iter().zip(&senders) {
                session.apply(tx.clone(), *sender).unwrap();
            }
        }
        let barrier = std::sync::Barrier::new(sessions.len());
        std::thread::scope(|scope| {
            for session in sessions {
                let barrier = &barrier;
                scope.spawn(move || {
                    barrier.wait();
                    assert_eq!(session.finish().unwrap().state_root, target.header.state_root);
                });
            }
        });
    }
}

/// A block with no transaction leaves no sealed state (Arkiv charges nothing for it). It is accepted with the
/// state unchanged, as arkiv-reth accepts it, and committed under its own hash, so the next block, empty or not,
/// finds its parent at the head.
#[test]
fn empty_blocks_are_accepted_and_the_chain_continues() {
    let node = Node::open(RABDI_GENESIS, None).unwrap();
    let blocks = rabdi_blocks();
    for (block, senders) in blocks.iter().take(10) {
        node.execute_block(block.clone(), senders.clone(), Apply::Commit).unwrap();
    }
    let (_, _, root) = node.head().unwrap();
    let empty_after = |parent: &alloy_consensus::Header| {
        let mut header = parent.clone();
        header.parent_hash = parent.hash_slow();
        header.number = parent.number + 1;
        header.timestamp = parent.timestamp + 2;
        header.gas_used = 0;
        header.state_root = root;
        Block { header, body: reth_ethereum::BlockBody { transactions: vec![], ommers: vec![], withdrawals: Some(Default::default()) } }
    };

    let first = empty_after(&blocks[9].0.header);
    assert_eq!(node.execute_block(first.clone(), vec![], Apply::DryRun).unwrap().state_root, root, "a dry run");
    assert_eq!(node.execute_block(first.clone(), vec![], Apply::Commit).unwrap().state_root, root);
    assert_eq!(node.head().unwrap().1, first.header.hash_slow());
    let second = empty_after(&first.header);
    node.execute_block(second.clone(), vec![], Apply::Commit).unwrap();
    let (_, tag, head_root) = node.head().unwrap();
    assert_eq!((tag, head_root), (second.header.hash_slow(), root), "the head names the latest block, its state unchanged");

    let session = node.build_begin(empty_after(&second.header).header).unwrap();
    assert_eq!(session.finish().unwrap().state_root, root, "a build with nothing applied keeps the parent's root");
}

/// Executions that do not commit (as block validation without import does) run side by side without dropping each
/// other's seal.
#[test]
fn concurrent_dry_runs_of_one_block_all_finish() {
    let node = Node::open(RABDI_GENESIS, None).unwrap();
    let blocks = rabdi_blocks();
    let (target, senders) = blocks.iter().find(|(block, _)| block.body.transactions.len() > 1).unwrap().clone();
    for (block, block_senders) in blocks.iter().take_while(|(block, _)| block.header.number < target.header.number) {
        node.execute_block(block.clone(), block_senders.clone(), Apply::Commit).unwrap();
    }
    for _ in 0..20 {
        let barrier = std::sync::Barrier::new(4);
        std::thread::scope(|scope| {
            for _ in 0..4 {
                let (node, barrier, target, senders) = (&node, &barrier, &target, &senders);
                scope.spawn(move || {
                    barrier.wait();
                    assert_eq!(node.execute_block(target.clone(), senders.clone(), Apply::DryRun).unwrap().state_root, target.header.state_root);
                });
            }
        });
    }
}

/// Blocks held rather than committed, each adopted once its child arrives (as the host does at the tip), end in
/// the same store, with the same results, as blocks committed one by one.
#[test]
fn held_blocks_adopted_by_their_children_match_committed_ones() {
    let blocks = rabdi_blocks();
    let committed = Node::open(RABDI_GENESIS, None).unwrap();
    let held = Node::open(RABDI_GENESIS, None).unwrap();
    for (block, senders) in &blocks {
        let expected = committed.execute_block(block.clone(), senders.clone(), Apply::Commit).unwrap();
        if block.header.number > 1 {
            held.adopt(block.header.parent_hash).unwrap();
        }
        let result = held.execute_block(block.clone(), senders.clone(), Apply::Hold).unwrap();
        assert_eq!(result.encode(), expected.encode(), "block {}", block.header.number);
    }
    let (_, tag, _) = held.head().unwrap();
    assert_eq!(tag, blocks[blocks.len() - 2].0.header.hash_slow(), "the last block is still held");
    held.adopt(blocks.last().unwrap().0.header.hash_slow()).unwrap();
    assert_eq!(held.head().unwrap(), committed.head().unwrap());
}

/// Two blocks at one height, as a late block and the one that replaces it: both execute over the same head while
/// held, and whichever is adopted commits while the other leaves no trace. No store copy is needed to switch.
#[test]
fn a_held_block_loses_to_its_sibling_at_no_cost() {
    let node = Node::open(RABDI_GENESIS, None).unwrap();
    let blocks = rabdi_blocks();
    let (target, senders) = blocks.iter().find(|(block, _)| block.body.transactions.len() > 1).unwrap().clone();
    for (block, block_senders) in blocks.iter().take_while(|(block, _)| block.header.number < target.header.number) {
        node.execute_block(block.clone(), block_senders.clone(), Apply::Commit).unwrap();
    }
    let parent = node.head().unwrap();

    // The sibling carries the protocol purge alone, its root the one a build of that reaches.
    let session = node.build_begin(target.header.clone()).unwrap();
    session.apply(target.body.transactions[0].clone(), senders[0]).unwrap();
    let mut sibling = target.clone();
    sibling.header.state_root = session.finish().unwrap().state_root;
    sibling.body.transactions.truncate(1);
    assert_ne!(sibling.header.state_root, target.header.state_root);

    node.execute_block(target.clone(), senders.clone(), Apply::Hold).unwrap();
    node.execute_block(sibling.clone(), senders[..1].to_vec(), Apply::Hold).unwrap();
    assert_eq!(node.head().unwrap(), parent, "nothing commits while held");

    node.adopt(sibling.header.hash_slow()).unwrap();
    let (_, tag, root) = node.head().unwrap();
    assert_eq!((tag, root), (sibling.header.hash_slow(), sibling.header.state_root));
    assert_eq!(node.adopt(target.header.hash_slow()).unwrap_err().0, Status::NotOnHead, "the loser went with the adoption");

    // The chain goes on from the sibling.
    let mut child = sibling.clone();
    child.header.parent_hash = sibling.header.hash_slow();
    child.header.number += 1;
    child.header.timestamp += 2;
    child.body.transactions.clear();
    node.execute_block(child.clone(), vec![], Apply::Commit).unwrap();
    assert_eq!(node.head().unwrap().1, child.header.hash_slow());
}

/// An empty block held at the tip loses to its sibling as a block with state does.
#[test]
fn a_held_empty_block_loses_to_its_sibling() {
    let node = Node::open(RABDI_GENESIS, None).unwrap();
    let blocks = rabdi_blocks();
    for (block, senders) in blocks.iter().take(10) {
        node.execute_block(block.clone(), senders.clone(), Apply::Commit).unwrap();
    }
    let parent = node.head().unwrap();
    let empty = |timestamp_step: u64| {
        let mut header = blocks[9].0.header.clone();
        header.parent_hash = blocks[9].0.header.hash_slow();
        header.number += 1;
        header.timestamp += timestamp_step;
        header.gas_used = 0;
        header.state_root = parent.2;
        Block { header, body: reth_ethereum::BlockBody { transactions: vec![], ommers: vec![], withdrawals: Some(Default::default()) } }
    };
    let (first, second) = (empty(2), empty(4));
    node.execute_block(first.clone(), vec![], Apply::Hold).unwrap();
    node.execute_block(second.clone(), vec![], Apply::Hold).unwrap();
    assert_eq!(node.head().unwrap(), parent);
    node.adopt(second.header.hash_slow()).unwrap();
    assert_eq!(node.head().unwrap().1, second.header.hash_slow());
    assert_eq!(node.adopt(first.header.hash_slow()).unwrap_err().0, Status::NotOnHead);
}

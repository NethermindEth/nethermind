//! replay GENESIS BLOCKS.jsonl [STORE_DIR] — execute blocks taken from an `eth_getBlockByNumber(n, true)` dump
//! through the shim, in order, committing each, and stop at the first whose state root is not the header's.
//! A development check of the native layer alone; the node path is the Nethermind plugin.

use std::io::BufRead;
use std::time::Instant;

use alloy_primitives::Address;
use arkiv_ffi::{Apply, Node};

fn main() -> eyre::Result<()> {
    let args: Vec<String> = std::env::args().collect();
    let genesis = std::fs::read_to_string(&args[1])?;
    let store = args.get(3).map(std::path::PathBuf::from);
    let node = Node::open(&genesis, store.as_deref())?;
    let started = Instant::now();
    let (mut blocks, mut txs) = (0u64, 0u64);
    for line in std::io::BufReader::new(std::fs::File::open(&args[2])?).lines() {
        let rpc: alloy_rpc_types_eth::Block = serde_json::from_str(&line?)?;
        let senders: Vec<Address> = rpc.transactions.txns().map(|tx| tx.inner.signer()).collect();
        let hash = rpc.header.hash;
        let block: reth_ethereum::Block = rpc.into_consensus().map_transactions(|tx: alloy_rpc_types_eth::Transaction| reth_ethereum::TransactionSigned::from(tx.into_inner()));
        let number = block.header.number;
        if alloy_primitives::keccak256(alloy_rlp::encode(&block.header)) != hash {
            eyre::bail!("block {number}: the decoded header does not hash to {hash}");
        }
        txs += block.body.transactions.len() as u64;
        match node.execute_block(block, senders, Apply::Commit) {
            Ok(_) => blocks += 1,
            Err((status, message)) => eyre::bail!("block {number}: {status:?}: {message}"),
        }
        if blocks % 500 == 0 {
            eprintln!("{blocks} blocks, {txs} txs, {:.1} blocks/s", blocks as f64 / started.elapsed().as_secs_f64());
        }
    }
    let (commit, tag, root) = node.head()?;
    println!("ok: {blocks} blocks, {txs} txs in {:.2?}; head commit {commit} block {tag} root {root}", started.elapsed());
    Ok(())
}

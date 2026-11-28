//! payloads [--rlp] BLOCKS > PAYLOADS.jsonl — turn blocks into Engine API `engine_newPayloadV4` parameter lists,
//! checking each block's hash on the way. BLOCKS is either `eth_getBlockByNumber(n, true)` JSON, one per line, or with
//! `--rlp` raw blocks as `debug_getRawBlock` returns them (0x-hex, one per line). Feeds `tools/engine_feed.py`, which
//! replays the chain into Nethermind and arkiv-reth alike for the benchmark.

use std::io::{BufRead, Write};

use alloy_primitives::{B256, Bytes, keccak256};
use alloy_rlp::Decodable;
use alloy_rpc_types_engine::ExecutionPayloadV3;

type Block = alloy_consensus::Block<reth_ethereum::TransactionSigned>;

fn main() -> eyre::Result<()> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let rlp = args.iter().any(|a| a == "--rlp");
    let path = args.iter().find(|a| !a.starts_with("--")).expect("BLOCKS");
    let stdout = std::io::stdout();
    let mut out = std::io::BufWriter::new(stdout.lock());
    let mut expected_number = None;
    for line in std::io::BufReader::new(std::fs::File::open(path)?).lines() {
        let line = line?;
        let (block, hash): (Block, B256) = if rlp {
            let bytes = alloy_primitives::hex::decode(line.trim())?;
            let block = Block::decode(&mut &bytes[..])?;
            let hash = keccak256(alloy_rlp::encode(&block.header));
            (block, hash)
        } else {
            let rpc: alloy_rpc_types_eth::Block = serde_json::from_str(&line)?;
            let hash = rpc.header.hash;
            let block = rpc.into_consensus().map_transactions(|tx: alloy_rpc_types_eth::Transaction| tx.into_inner().into());
            eyre::ensure!(keccak256(alloy_rlp::encode(&block.header)) == hash, "block {}: the payload does not hash to {hash}", block.header.number);
            (block, hash)
        };
        // A gap or a repeat would make the replay meaningless; the corpus is one contiguous chain.
        if let Some(n) = expected_number {
            eyre::ensure!(block.header.number == n, "block {} where {n} was expected", block.header.number);
        }
        expected_number = Some(block.header.number + 1);
        let parent_beacon_block_root = block.header.parent_beacon_block_root.expect("a Cancun header");
        let payload = ExecutionPayloadV3::from_block_unchecked(hash, &block);
        let params = serde_json::json!([payload, Vec::<B256>::new(), parent_beacon_block_root, Vec::<Bytes>::new()]);
        writeln!(out, "{}", serde_json::to_string(&params)?)?;
    }
    Ok(())
}

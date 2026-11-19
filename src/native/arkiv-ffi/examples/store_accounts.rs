//! store_accounts GENESIS_JSON STORE_DIR — what state sync does with a received store, timed: list its accounts
//! (`store::account_addresses`), open it, read its head and every account.

use std::time::Instant;

fn main() -> eyre::Result<()> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let genesis = std::fs::read_to_string(&args[0])?;
    let dir = std::path::Path::new(&args[1]);
    let t = Instant::now();
    let addresses = arkiv_ffi::store::account_addresses(dir)?;
    println!("listed {} accounts in {:.2?}", addresses.len(), t.elapsed());
    let t = Instant::now();
    let node = arkiv_ffi::Node::open(&genesis, Some(dir))?;
    let (commit, tag, root) = node.head()?;
    println!("opened in {:.2?}: head commit {commit}, block {tag}, root {root}", t.elapsed());
    let t = Instant::now();
    let mut held = 0;
    for address in &addresses {
        if node.account(*address)?.is_some() {
            held += 1;
        }
    }
    println!("read {held} accounts in {:.2?}", t.elapsed());
    Ok(())
}

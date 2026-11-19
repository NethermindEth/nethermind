//! devtx CHAIN_ID KEY_INDEX NONCE KIND [ARG] — one signed EIP-1559 transaction for a dev chain, as raw 0x-hex, from
//! the Hardhat test mnemonic's account KEY_INDEX (the accounts Arkiv's genesis/dev.json funds). KIND:
//!   create LIFETIME   execute([create]) of an entity with two indexed attributes, living LIFETIME blocks
//!   transfer TO       1 gwei to TO
//!   deploy            a contract creation, which Arkiv refuses
//! Used by tools/devnet_build.sh to put real Arkiv transactions in a pool.

use alloy_consensus::{SignableTransaction, TxEip1559, TxEnvelope};
use alloy_network::TxSignerSync;
use alloy_primitives::{Address, Bytes, TxKind, U256};
use alloy_signer_local::{MnemonicBuilder, coins_bip39::English};
use alloy_sol_types::SolCall;
use arkiv_bindings::{Attribute, IEntityRegistry, Operation};
use arkiv_interfaces::entity::AttributeValue;

fn ident(name: &str) -> arkiv_bindings::Ident32 {
    let mut bytes = [0u8; 32];
    bytes[..name.len()].copy_from_slice(name.as_bytes());
    arkiv_bindings::Ident32::from(alloy_primitives::FixedBytes::<32>::from(bytes))
}

fn main() -> eyre::Result<()> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let chain_id: u64 = args[0].parse()?;
    let index: u32 = args[1].parse()?;
    let nonce: u64 = args[2].parse()?;
    let signer = MnemonicBuilder::<English>::default()
        .phrase("test test test test test test test test test test test junk")
        .index(index)?
        .build()?;
    let (to, input, value) = match args[3].as_str() {
        "create" => {
            let lifetime: u64 = args[4].parse()?;
            let attributes = vec![
                Attribute::from_value(ident("app"), &AttributeValue::Str("nethermind-build".into()), true)?,
                Attribute::from_value(ident("n"), &AttributeValue::U64(nonce), true)?,
            ];
            let op = Operation::create(u128::from(nonce) + 1, 0, lifetime, 0, attributes);
            let data = IEntityRegistry::executeCall { ops: vec![op] }.abi_encode();
            (arkiv_reth_executor::ARKIV_ADDRESS, Bytes::from(data), U256::ZERO)
        }
        "transfer" => (args[4].parse::<Address>()?, Bytes::new(), U256::from(1_000_000_000u64)),
        "deploy" => (Address::ZERO, Bytes::from_static(&[0x60, 0x00, 0x60, 0x00, 0xf3]), U256::ZERO),
        other => eyre::bail!("unknown kind {other}"),
    };
    let mut tx = TxEip1559 {
        chain_id,
        nonce,
        gas_limit: 2_000_000,
        max_fee_per_gas: 10_000_000_000,
        max_priority_fee_per_gas: 1_000_000_000,
        to: if args[3] == "deploy" { TxKind::Create } else { TxKind::Call(to) },
        value,
        input,
        ..Default::default()
    };
    let signature = signer.sign_transaction_sync(&mut tx)?;
    let envelope = TxEnvelope::Eip1559(tx.into_signed(signature));
    println!("0x{}", alloy_primitives::hex::encode(alloy_eips::eip2718::Encodable2718::encoded_2718(&envelope)));
    Ok(())
}

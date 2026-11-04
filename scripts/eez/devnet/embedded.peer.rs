use std::{
    collections::BTreeMap,
    fs,
    path::Path,
    time::{Duration, Instant},
};

use alloy_consensus::{
    Header,
    proofs::{calculate_receipt_root, calculate_transaction_root},
};
use alloy_genesis::Genesis;
use alloy_primitives::{B256, KECCAK256_EMPTY, keccak256};
use alloy_rlp::{Decodable, Encodable, RlpDecodable, RlpEncodable};
use anyhow::{Context, Result, bail, ensure};
use futures_util::{SinkExt, StreamExt};
use reth_chainspec::{ChainSpec, Head};
use reth_ecies::stream::ECIESStream;
use reth_eth_wire::{
    BasicNetworkPrimitives, BlockBodies, BlockHeaders, Capability, EthVersion, GetBlockHeaders,
    HeadersDirection, HelloMessageWithProtocols, P2PStream, Receipts, UnauthedEthStream,
    UnauthedP2PStream, UnifiedStatus,
    protocol::Protocol,
    snap::{
        AccountRangeMessage, ByteCodesMessage, GetAccountRangeMessage, GetByteCodesMessage,
        GetStorageRangesMessage, StorageRangesMessage,
    },
};
use reth_network_peers::{NodeRecord, pk2id};
use reth_trie_common::{
    EMPTY_ROOT_HASH, TrieAccount,
    range_proof::verify_range_proof,
    root::{state_root, storage_root},
};
use secp256k1::{SECP256K1, SecretKey};
use serde_json::{Value, json};
use tokio::net::TcpStream;

// Use the selected source's native transaction and receipt codecs on the wire.
type Native = BasicNetworkPrimitives<eez_primitives::EezPrimitives, eez_primitives::EezTxEnvelope>;
type Transport = P2PStream<ECIESStream<TcpStream>>;
const MAXIMUM: B256 = B256::repeat_byte(0xff);
const PAGE_BYTES: u64 = 4096;
const MAX_PAGES: usize = 128;

#[derive(RlpEncodable, RlpDecodable)]
struct Pair<T> {
    request_id: u64,
    message: T,
}

struct Probe {
    transport: Transport,
    request_id: u64,
    requests: usize,
    responses: usize,
    bytes: usize,
    snap: Capability,
}

impl Probe {
    async fn connect(enode: &str, spec: &ChainSpec, header: &Header, hash: B256) -> Result<Self> {
        let remote: NodeRecord = enode.parse()?;
        let key = SecretKey::from_slice(
            alloy_signer_local::PrivateKeySigner::random()
                .to_bytes()
                .as_slice(),
        )?;
        let socket = TcpStream::connect(remote.tcp_addr()).await?;
        let encrypted = ECIESStream::connect(socket, key, remote.id).await?;
        let snap = Capability::new_static("snap", 1);
        let mut hello =
            HelloMessageWithProtocols::builder(pk2id(&key.public_key(SECP256K1))).build();
        hello.client_version = "eez-state-probe/1".into();
        hello.port = 0;
        hello.protocols = vec![EthVersion::Eth68.into(), Protocol::new(snap.clone(), 8)];
        let (transport, _) = UnauthedP2PStream::new(encrypted).handshake(hello).await?;
        ensure!(
            transport.shared_capabilities().find(&snap).is_some(),
            "snap/1 was not negotiated"
        );
        let head = Head {
            number: header.number,
            hash,
            timestamp: header.timestamp,
            ..Default::default()
        };
        let mut status = UnifiedStatus::spec_builder(spec, &head);
        status.set_eth_version(EthVersion::Eth68);
        let (eth, remote) = UnauthedEthStream::new(transport)
            .handshake::<Native>(status, spec.fork_filter(head))
            .await?;
        ensure!(
            remote.genesis == status.genesis && remote.chain == status.chain,
            "wire chain identity differs"
        );
        Ok(Self {
            transport: eth.into_inner(),
            request_id: 0,
            requests: 0,
            responses: 0,
            bytes: 0,
            snap,
        })
    }

    fn next_id(&mut self) -> u64 {
        self.request_id += 1;
        self.request_id
    }

    async fn exchange<T: Encodable, R: Decodable>(
        &mut self,
        capability: &Capability,
        id: u8,
        response_id: u8,
        request: T,
    ) -> Result<R> {
        let id = self
            .transport
            .shared_capabilities()
            .relative_message_id(capability, id)
            .context("request capability missing")?;
        let response_id = self
            .transport
            .shared_capabilities()
            .relative_message_id(capability, response_id)
            .context("response capability missing")?;
        let mut outgoing = vec![id];
        request.encode(&mut outgoing);
        self.transport.send(outgoing.into()).await?;
        self.requests += 1;
        for _ in 0..32 {
            let incoming = tokio::time::timeout(Duration::from_secs(10), self.transport.next())
                .await?
                .context("peer closed the stream")??;
            ensure!(
                incoming.len() <= 2 * 1024 * 1024,
                "test response exceeds its bound"
            );
            if incoming.first() == Some(&response_id) {
                self.responses += 1;
                self.bytes += incoming.len();
                return Ok(alloy_rlp::decode_exact(&incoming[1..])?);
            }
            // Peers can query our advertised head during admission. This probe has no serving DB.
            if matches!(incoming.first(), Some(3 | 5 | 15)) {
                let mut payload = &incoming[1..];
                let list = alloy_rlp::Header::decode(&mut payload)?;
                ensure!(list.list, "invalid unsolicited ETH request");
                let request_id = u64::decode(&mut payload)?;
                let response = Pair {
                    request_id,
                    message: Vec::<B256>::new(),
                };
                let mut bytes = vec![incoming[0] + 1];
                response.encode(&mut bytes);
                self.transport.send(bytes.into()).await?;
            } else {
                bail!("unexpected wire message id {:?}", incoming.first());
            }
        }
        bail!("too many unsolicited messages");
    }

    async fn history(&mut self, expected: &Header, hash: B256) -> Result<(usize, Vec<u8>)> {
        let eth = Capability::new_static("eth", 68);
        let request_id = self.next_id();
        let headers: Pair<BlockHeaders<Header>> = self
            .exchange(
                &eth,
                3,
                4,
                Pair {
                    request_id,
                    message: GetBlockHeaders {
                        start_block: hash.into(),
                        limit: 1,
                        skip: 0,
                        direction: HeadersDirection::Rising,
                    },
                },
            )
            .await?;
        ensure!(
            headers.request_id == request_id && headers.message.0.len() == 1,
            "header response identity/count"
        );
        ensure!(
            &headers.message.0[0] == expected && expected.hash_slow() == hash,
            "wire header differs"
        );
        let request_id = self.next_id();
        let bodies: Pair<BlockBodies<eez_primitives::BlockBody>> = self
            .exchange(
                &eth,
                5,
                6,
                Pair {
                    request_id,
                    message: vec![hash],
                },
            )
            .await?;
        ensure!(
            bodies.request_id == request_id && bodies.message.0.len() == 1,
            "body response identity/count"
        );
        let transactions = &bodies.message.0[0].transactions;
        ensure!(
            !transactions.is_empty(),
            "non-genesis fixture must include native transactions"
        );
        ensure!(
            calculate_transaction_root(transactions) == expected.transactions_root,
            "wire transaction root differs"
        );
        let types: Vec<u8> = transactions
            .iter()
            .map(|tx| alloy_eips::eip2718::Typed2718::ty(tx))
            .collect();
        ensure!(
            types.iter().any(|ty| matches!(ty, 0x45 | 0x46)),
            "fixture must transfer native transaction envelopes"
        );
        let request_id = self.next_id();
        let receipts: Pair<Receipts<eez_primitives::Receipt>> = self
            .exchange(
                &eth,
                15,
                16,
                Pair {
                    request_id,
                    message: vec![hash],
                },
            )
            .await?;
        ensure!(
            receipts.request_id == request_id && receipts.message.0.len() == 1,
            "receipt response identity/count"
        );
        ensure!(
            receipts.message.0[0].len() == transactions.len(),
            "wire receipt count differs"
        );
        ensure!(
            calculate_receipt_root(&receipts.message.0[0]) == expected.receipts_root,
            "wire receipt root differs"
        );
        Ok((transactions.len(), types))
    }

    async fn accounts(&mut self, root: B256) -> Result<BTreeMap<B256, TrieAccount>> {
        let mut accounts = BTreeMap::new();
        let mut origin = B256::ZERO;
        for _ in 0..MAX_PAGES {
            let request_id = self.next_id();
            let response: AccountRangeMessage = self
                .exchange(
                    &self.snap.clone(),
                    0,
                    1,
                    GetAccountRangeMessage {
                        request_id,
                        root_hash: root,
                        starting_hash: origin,
                        limit_hash: MAXIMUM,
                        response_bytes: PAGE_BYTES,
                    },
                )
                .await?;
            ensure!(
                response.request_id == request_id,
                "account request ID differs"
            );
            let entries: Vec<_> = response
                .accounts
                .into_iter()
                .map(|account| account.into_trie_entry())
                .collect();
            let next = verify_range_proof(
                root,
                origin,
                MAXIMUM,
                entries
                    .iter()
                    .map(|(hash, account)| (*hash, alloy_rlp::encode(account))),
                &response.proof,
            )?;
            for (hash, account) in &entries {
                ensure!(
                    accounts.insert(*hash, *account).is_none(),
                    "duplicate account page"
                );
            }
            ensure!(accounts.len() <= 10_000, "fixture account bound exceeded");
            match next {
                None => {
                    ensure!(!accounts.is_empty(), "snap returned no accounts");
                    ensure!(
                        state_root(accounts.iter().map(|(hash, account)| (*hash, *account)))
                            == root,
                        "complete account snapshot root differs"
                    );
                    return Ok(accounts);
                }
                Some(next) => {
                    ensure!(
                        entries.last().is_some_and(|(last, _)| next > *last) && next > origin,
                        "account range did not advance"
                    );
                    origin = next;
                }
            }
        }
        bail!("account page bound exceeded")
    }

    async fn storage(&mut self, root: B256, account: B256, expected: B256) -> Result<usize> {
        let mut slots = BTreeMap::new();
        let mut origin = B256::ZERO;
        for _ in 0..MAX_PAGES {
            let request_id = self.next_id();
            let response: StorageRangesMessage = self
                .exchange(
                    &self.snap.clone(),
                    2,
                    3,
                    GetStorageRangesMessage {
                        request_id,
                        root_hash: root,
                        account_hashes: vec![account],
                        starting_hash: origin.into(),
                        limit_hash: MAXIMUM.into(),
                        response_bytes: PAGE_BYTES,
                    },
                )
                .await?;
            ensure!(
                response.request_id == request_id && response.slots.len() == 1,
                "storage response identity/count"
            );
            let entries = &response.slots[0];
            let next = verify_range_proof(
                expected,
                origin,
                MAXIMUM,
                entries.iter().map(|slot| (slot.hash, slot.data.to_vec())),
                &response.proof,
            )?;
            for slot in entries {
                ensure!(
                    slots.insert(slot.hash, slot.value()?).is_none(),
                    "duplicate storage page"
                );
            }
            ensure!(slots.len() <= 50_000, "fixture storage bound exceeded");
            match next {
                None => {
                    ensure!(!slots.is_empty(), "nonempty storage root returned no slots");
                    let count = slots.len();
                    ensure!(
                        storage_root(slots.into_iter()) == expected,
                        "complete storage snapshot root differs"
                    );
                    return Ok(count);
                }
                Some(next) => {
                    ensure!(
                        entries.last().is_some_and(|last| next > last.hash) && next > origin,
                        "storage range did not advance"
                    );
                    origin = next;
                }
            }
        }
        bail!("storage page bound exceeded")
    }

    async fn code(&mut self, hash: B256) -> Result<usize> {
        let request_id = self.next_id();
        let response: ByteCodesMessage = self
            .exchange(
                &self.snap.clone(),
                4,
                5,
                GetByteCodesMessage {
                    request_id,
                    hashes: vec![hash],
                    response_bytes: 1024 * 1024,
                },
            )
            .await?;
        ensure!(
            response.request_id == request_id && response.codes.len() == 1,
            "code response identity/count"
        );
        ensure!(
            !response.codes[0].is_empty() && keccak256(&response.codes[0]) == hash,
            "wire bytecode hash differs"
        );
        Ok(response.codes[0].len())
    }
}

pub(super) async fn rpc(
    client: &reqwest::Client,
    url: &str,
    method: &str,
    params: Value,
) -> Result<Value> {
    let response: Value = client
        .post(url)
        .json(&json!({"jsonrpc":"2.0", "id":1, "method":method, "params":params}))
        .send()
        .await?
        .error_for_status()?
        .json()
        .await?;
    ensure!(
        response.get("error").is_none(),
        "{method}: {}",
        response["error"]
    );
    response
        .get("result")
        .cloned()
        .context("RPC result missing")
}

pub async fn verify(
    candidate: &str,
    reference: &str,
    genesis: &Path,
    settings: &Value,
) -> Result<()> {
    tokio::time::timeout(
        Duration::from_secs(90),
        verify_inner(candidate, reference, genesis, settings),
    )
    .await?
}

async fn verify_inner(
    candidate: &str,
    reference: &str,
    genesis: &Path,
    settings: &Value,
) -> Result<()> {
    let started = Instant::now();
    let client = reqwest::Client::builder()
        .timeout(Duration::from_secs(5))
        .build()?;
    let spec = ChainSpec::from_genesis(serde_json::from_slice::<Genesis>(&fs::read(genesis)?)?);
    let tip = rpc(
        &client,
        candidate,
        "eth_getBlockByNumber",
        json!(["latest", true]),
    )
    .await?;
    let number = u64::from_str_radix(
        tip["number"]
            .as_str()
            .context("head number missing")?
            .trim_start_matches("0x"),
        16,
    )?;
    let mut selected = None;
    for height in (number.saturating_sub(31).max(1)..=number).rev() {
        let block = if height == number {
            tip.clone()
        } else {
            rpc(
                &client,
                candidate,
                "eth_getBlockByNumber",
                json!([format!("{height:#x}"), true]),
            )
            .await?
        };
        if block["transactions"]
            .as_array()
            .is_some_and(|transactions| {
                transactions
                    .iter()
                    .any(|tx| matches!(tx["type"].as_str(), Some("0x45" | "0x46")))
            })
        {
            selected = Some(block);
            break;
        }
    }
    let latest = selected.context("no recent native transaction block for wire verification")?;
    let header: Header = serde_json::from_value(latest.clone())?;
    let hash: B256 = serde_json::from_value(latest["hash"].clone())?;
    ensure!(
        header.number > 0 && header.state_root != EMPTY_ROOT_HASH,
        "snap requires a nonempty non-genesis pivot"
    );
    let canonical = rpc(
        &client,
        reference,
        "eth_getBlockByNumber",
        json!([latest["number"], false]),
    )
    .await?;
    ensure!(
        canonical["hash"] == latest["hash"] && canonical["stateRoot"] == latest["stateRoot"],
        "pivot differs from source"
    );
    let info = rpc(&client, candidate, "admin_nodeInfo", json!([])).await?;
    let enode = info["enode"].as_str().context("node enode missing")?;
    let mut probe = Probe::connect(enode, &spec, &header, hash).await?;
    let (transactions, types) = probe.history(&header, hash).await?;
    let accounts = probe.accounts(header.state_root).await?;
    let mut slots = 0;
    let mut code_bytes = 0;
    let mut codes = std::collections::BTreeSet::new();
    for (hash, account) in &accounts {
        if account.storage_root != EMPTY_ROOT_HASH {
            slots += probe
                .storage(header.state_root, *hash, account.storage_root)
                .await?;
        }
        if account.code_hash != KECCAK256_EMPTY && codes.insert(account.code_hash) {
            code_bytes += probe.code(account.code_hash).await?;
        }
    }
    ensure!(
        slots > 0 && code_bytes > 0,
        "fixture must transfer real storage and bytecode"
    );
    for url in [candidate, reference] {
        let recheck = rpc(
            &client,
            url,
            "eth_getBlockByNumber",
            json!([latest["number"], false]),
        )
        .await?;
        ensure!(
            recheck["hash"] == latest["hash"],
            "pivot reorged during state transfer"
        );
    }
    let observation_path = Path::new(
        settings["result_directory"]
            .as_str()
            .context("result directory missing")?,
    )
    .join(format!(
        "{}-{}.json",
        settings["case"].as_str().unwrap(),
        settings["phase"].as_str().unwrap()
    ));
    let observation: Value = serde_json::from_slice(&fs::read(observation_path)?)?;
    ensure!(
        observation["candidate_revision"] == settings["candidate_revision"]
            && observation["image_id"] == settings["image_id"],
        "peer candidate observation identity differs"
    );
    let result = json!({"result":"pass", "scope":"snap/1-state-serving", "full_node_bootstrap":false,
        "candidate_revision":settings["candidate_revision"], "image_id":settings["image_id"],
        "case":settings["case"], "phase":settings["phase"], "container":observation["container"],
        "pivot":latest["number"], "hash":hash, "state_root":header.state_root,
        "transactions":transactions, "transaction_types":types, "accounts":accounts.len(),
        "storage_slots":slots, "code_bytes":code_bytes, "code_hashes":codes.len(),
        "wire_requests":probe.requests,"wire_responses":probe.responses,"wire_response_bytes":probe.bytes,"elapsed_ms":started.elapsed().as_millis()});
    let path = Path::new(
        settings["result_directory"]
            .as_str()
            .context("result directory missing")?,
    )
    .join(format!(
        "{}-{}-peer.json",
        settings["case"].as_str().unwrap(),
        settings["phase"].as_str().unwrap()
    ));
    fs::write(path, serde_json::to_vec_pretty(&result)?)?;
    println!("{result}");
    Ok(())
}

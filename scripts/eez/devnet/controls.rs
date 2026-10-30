use std::{env, fs, path::PathBuf, time::Duration};

use alloy_primitives::hex;
use anyhow::{Context, Result, bail, ensure};
use eez_control_rpc::{
    v1::{BlockWitness, ExecutionWitness},
    v2::{
        Begin, Block, Cancel, ClientFrame, Resume, Rewind, ServerFrame, client_frame,
        prover_client::ProverClient, server_frame,
    },
};
use serde_json::{Value, json};
use tokio::{sync::mpsc, time::timeout};
use tokio_stream::wrappers::ReceiverStream;
use tonic::{Streaming, transport::Channel};

const TIMEOUT: Duration = Duration::from_secs(30);

struct Session {
    sender: mpsc::Sender<ClientFrame>,
    responses: Streaming<ServerFrame>,
    id: Vec<u8>,
    epoch: u64,
    sequence: u64,
}

impl Session {
    async fn open(
        client: &mut ProverClient<Channel>,
        id: Vec<u8>,
        kind: client_frame::Kind,
    ) -> Result<Self> {
        let (sender, receiver) = mpsc::channel(4);
        sender
            .send(ClientFrame {
                session_id: id.clone(),
                request_id: 1,
                epoch: 0,
                kind: Some(kind),
            })
            .await?;
        let responses = timeout(TIMEOUT, client.prove_stream(ReceiverStream::new(receiver)))
            .await??
            .into_inner();
        Ok(Self {
            sender,
            responses,
            id,
            epoch: 0,
            sequence: 1,
        })
    }

    async fn send(&mut self, kind: client_frame::Kind) -> Result<u64> {
        self.sequence += 1;
        timeout(
            TIMEOUT,
            self.sender.send(ClientFrame {
                session_id: self.id.clone(),
                request_id: self.sequence,
                epoch: self.epoch,
                kind: Some(kind),
            }),
        )
        .await??;
        Ok(self.sequence)
    }

    async fn response(&mut self) -> Result<ServerFrame> {
        let frame = timeout(TIMEOUT, self.responses.message())
            .await??
            .context("missing control response")?;
        eprintln!("live control response: {frame:?}");
        Ok(frame)
    }

    async fn ready(
        &mut self,
        request: u64,
        number: u64,
        hash: &[u8],
        previous: Option<u64>,
    ) -> Result<()> {
        let frame = self.response().await?;
        ensure!(
            frame.request_id == request && frame.session_id.len() == 32,
            "Ready correlation: {frame:?}"
        );
        ensure!(
            frame.epoch != 0 && Some(frame.epoch) != previous,
            "Ready must issue a new epoch: {frame:?}"
        );
        if !self.id.is_empty() {
            ensure!(
                frame.session_id == self.id,
                "Ready changed session identity"
            );
        }
        let Some(server_frame::Kind::Ready(cursor)) = frame.kind else {
            bail!("expected Ready: {frame:?}");
        };
        ensure!(
            cursor.validated_through == number && cursor.validated_hash == hash,
            "Ready must name the fully validated cursor"
        );
        self.id = frame.session_id;
        self.epoch = frame.epoch;
        Ok(())
    }

    fn validated(
        &self,
        frame: ServerFrame,
        request: u64,
        block: &BlockWitness,
        root: &[u8],
    ) -> Result<()> {
        ensure!(
            frame.request_id == request && frame.session_id == self.id && frame.epoch == self.epoch,
            "Validated correlation: {frame:?}"
        );
        let Some(server_frame::Kind::Validated(value)) = frame.kind else {
            bail!("expected Validated: {frame:?}");
        };
        ensure!(
            value.number == block.number
                && value.hash == block.hash
                && value.post_state_root == root,
            "Validated must match the real block and state root"
        );
        Ok(())
    }

    async fn append(&mut self, block: &BlockWitness, root: &[u8]) -> Result<()> {
        let request = self
            .send(client_frame::Kind::Block(Block {
                data: Some(block.clone()),
            }))
            .await?;
        let response = self.response().await?;
        self.validated(response, request, block, root)
    }

    async fn cancel(mut self, queued: Option<(&BlockWitness, &[u8])>) -> Result<()> {
        let pending = if let Some((block, _)) = queued {
            Some(
                self.send(client_frame::Kind::Block(Block {
                    data: Some(block.clone()),
                }))
                .await?,
            )
        } else {
            None
        };
        let request = self.send(client_frame::Kind::Cancel(Cancel {})).await?;
        let mut response = self.response().await?;
        if let (Some(pending), Some((block, root))) = (pending, queued) {
            if response.request_id == pending {
                self.validated(response, pending, block, root)?;
                response = self.response().await?;
            }
        }
        ensure!(
            response.request_id == request
                && response.session_id == self.id
                && response.epoch == self.epoch,
            "Cancel must echo the retired binding: {response:?}"
        );
        ensure!(
            matches!(response.kind, Some(server_frame::Kind::Cancelled(_))),
            "expected Cancelled: {response:?}"
        );
        drop(self.sender);
        ensure!(
            timeout(TIMEOUT, self.responses.message()).await??.is_none(),
            "no frames may follow Cancelled"
        );
        Ok(())
    }
}

fn bytes(value: &Value) -> Result<Vec<u8>> {
    let text = value.as_str().context("missing input bytes")?;
    Ok(hex::decode(
        text.strip_prefix("0x").context("missing hex prefix")?,
    )?)
}

fn items(value: &Value) -> Result<Vec<Vec<u8>>> {
    value
        .as_array()
        .context("missing witness list")?
        .iter()
        .map(bytes)
        .collect()
}

fn block(value: &Value) -> Result<(BlockWitness, Vec<u8>)> {
    let witness = &value["witness"];
    Ok((
        BlockWitness {
            number: value["number"].as_u64().context("missing block number")?,
            hash: bytes(&value["hash"])?,
            parent_hash: bytes(&value["parent_hash"])?,
            rlp: bytes(&value["rlp"])?,
            witness: Some(ExecutionWitness {
                state: items(&witness["state"])?,
                codes: items(&witness["codes"])?,
                keys: items(&witness["keys"])?,
                headers: items(&witness["headers"])?,
            }),
        },
        bytes(&value["state_root"])?,
    ))
}

#[tokio::test]
async fn live_blocks_resume_rewind_cancel_preserve_cursor_and_session_isolation() -> Result<()> {
    let directory = PathBuf::from(env::var("EEZ_CI_RESULT_DIR")?);
    let input_path = directory.join("streaming-controls-input.json");
    ensure!(
        fs::metadata(&input_path)?.len() <= 32 * 1024 * 1024,
        "control input exceeds limit"
    );
    let input: Value = serde_json::from_slice(&fs::read(input_path)?)?;
    let values = input["blocks"]
        .as_array()
        .context("missing control blocks")?;
    ensure!(
        values.len() == 2,
        "control probe requires two real consecutive blocks"
    );
    let (first, first_root) = block(&values[0])?;
    let (second, second_root) = block(&values[1])?;
    let anchor_hash = bytes(&input["anchor_hash"])?;
    let anchor_number = input["anchor_number"]
        .as_u64()
        .context("missing anchor number")?;
    ensure!(
        first.number == anchor_number + 1 && first.parent_hash == anchor_hash,
        "first block must extend the anchor"
    );
    ensure!(
        second.number == first.number + 1 && second.parent_hash == first.hash,
        "second block must extend the first"
    );
    let begin = Begin {
        rollup_id: input["rollup_id"].as_u64().context("missing rollup id")?,
        anchor_number,
        anchor_hash: anchor_hash.clone(),
        anchor_state_root: bytes(&input["anchor_state_root"])?,
    };
    let endpoint = env::var("EEZ_CONTROL_ENDPOINT")?;
    let channel = Channel::from_shared(endpoint)?
        .connect_timeout(Duration::from_secs(5))
        .connect()
        .await?;
    let mut client = ProverClient::new(channel)
        .max_decoding_message_size(16 * 1024 * 1024)
        .max_encoding_message_size(16 * 1024 * 1024);
    let mut original = Session::open(
        &mut client,
        Vec::new(),
        client_frame::Kind::Begin(begin.clone()),
    )
    .await?;
    original.ready(1, anchor_number, &anchor_hash, None).await?;
    original.append(&first, &first_root).await?;
    let id = original.id.clone();
    let epoch = original.epoch;
    drop(original);

    let mut resumed = Session::open(
        &mut client,
        id.clone(),
        client_frame::Kind::Resume(Resume {}),
    )
    .await?;
    resumed
        .ready(1, first.number, &first.hash, Some(epoch))
        .await?;
    let epoch = resumed.epoch;
    let request = resumed
        .send(client_frame::Kind::Rewind(Rewind {
            ancestor_hash: anchor_hash.clone(),
        }))
        .await?;
    resumed
        .ready(request, anchor_number, &anchor_hash, Some(epoch))
        .await?;
    resumed.append(&first, &first_root).await?;

    let mut independent =
        Session::open(&mut client, Vec::new(), client_frame::Kind::Begin(begin)).await?;
    independent
        .ready(1, anchor_number, &anchor_hash, None)
        .await?;
    ensure!(
        independent.id != id,
        "Begin must allocate an independent session"
    );
    resumed.cancel(Some((&second, &second_root))).await?;

    let mut cancelled =
        Session::open(&mut client, id, client_frame::Kind::Resume(Resume {})).await?;
    let rejected = cancelled.response().await?;
    ensure!(
        rejected.request_id == 1 && rejected.session_id.is_empty() && rejected.epoch == 0,
        "cancelled session must not bind: {rejected:?}"
    );
    let Some(server_frame::Kind::Rejected(value)) = rejected.kind else {
        bail!("cancelled session resumed: {rejected:?}");
    };
    ensure!(
        value.code == 5 && value.validated_through == 0 && value.validated_hash.is_empty(),
        "cancelled session must be unavailable"
    );
    drop(cancelled);
    independent.append(&first, &first_root).await?;
    independent.cancel(None).await?;

    let blocks: Vec<Value> = values
        .iter()
        .map(|value| {
            json!({
                "number": value["number"], "hash": value["hash"], "state_root": value["state_root"],
            })
        })
        .collect();
    fs::write(
        directory.join("streaming-controls-probe.json"),
        serde_json::to_vec_pretty(&json!({
            "result": "pass", "anchor_hash": input["anchor_hash"], "blocks": blocks,
            "checks": ["disconnect-resume", "rewind-reexecute", "pipelined-cancel", "cancelled-resume-rejected", "independent-session"],
        }))?,
    )?;
    Ok(())
}

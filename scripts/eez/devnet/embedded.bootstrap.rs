use std::{
    fs,
    path::Path,
    time::{Duration, Instant},
};

use anyhow::{Context, Result, ensure};
use serde_json::{Value, json};

use super::{Candidate, peer};

pub async fn verify(server: &Candidate, reference: &str, settings: &Value) -> Result<()> {
    let started = Instant::now();
    let client = reqwest::Client::builder()
        .timeout(Duration::from_secs(5))
        .build()?;
    let l1 = settings["l1"].as_str().context("L1 URL missing")?;
    let rollup_id: u64 = settings["rollup_id"]
        .as_str()
        .context("rollup ID missing")?
        .parse()?;
    let topic = format!(
        "{:#x}",
        alloy_primitives::keccak256(b"L2ExecutionPerformed(uint64,bytes32,uint256)")
    );
    let until = Instant::now() + Duration::from_secs(90);
    let pivot = loop {
        let finalized = peer::rpc(
            &client,
            l1,
            "eth_getBlockByNumber",
            json!(["finalized", false]),
        )
        .await?;
        if quantity(&finalized["number"])?
            < settings["registry_deploy_block"]
                .as_u64()
                .context("deployment block missing")?
        {
            ensure!(
                Instant::now() < until,
                "L1 finality has not reached the deployment within 90s"
            );
            tokio::time::sleep(Duration::from_millis(250)).await;
            continue;
        }
        let logs = peer::rpc(&client, l1, "eth_getLogs", json!([{
            "address":settings["registry"], "fromBlock":format!("0x{:x}", settings["registry_deploy_block"].as_u64().context("deployment block missing")?),
            "toBlock":finalized["number"], "topics":[topic,format!("0x{rollup_id:064x}")]
        }])).await?;
        let mut endpoints: std::collections::BTreeMap<u64, ((u64, u64), &Value)> =
            std::collections::BTreeMap::new();
        for log in logs.as_array().context("settlement logs missing")? {
            let position = (
                quantity(&log["transactionIndex"])?,
                quantity(&log["logIndex"])?,
            );
            let block = quantity(&log["blockNumber"])?;
            if endpoints
                .get(&block)
                .is_none_or(|(previous, _)| position > *previous)
            {
                endpoints.insert(block, (position, log));
            }
        }
        let mut selected = None;
        for (_, log) in endpoints.values() {
            let data = log["data"].as_str().context("settlement data missing")?;
            ensure!(
                data.len() == 130 && data.starts_with("0x"),
                "invalid settlement data"
            );
            let candidate = peer::rpc(
                &client,
                &server.url(),
                "eth_getBlockByHash",
                json!([&data[..66], false]),
            )
            .await?;
            if !candidate.is_null() && quantity(&candidate["number"])? > 0 {
                selected = Some(candidate);
                break;
            }
        }
        if let Some(pivot) = selected {
            break pivot;
        }
        ensure!(
            Instant::now() < until,
            "fixture has no finalized non-genesis settlement within 90s"
        );
        tokio::time::sleep(Duration::from_millis(250)).await;
    };
    let pivot_number = quantity(&pivot["number"])?;
    let mut post_pivot_transactions = 0;
    for hash in settings["unsafe_transactions"]
        .as_array()
        .context("native source transactions missing")?
    {
        let receipt = peer::rpc(
            &client,
            &server.url(),
            "eth_getTransactionReceipt",
            json!([hash]),
        )
        .await?;
        post_pivot_transactions += usize::from(quantity(&receipt["blockNumber"])? > pivot_number);
    }
    ensure!(
        post_pivot_transactions > 0,
        "fixture must execute known transactions after the SNAP pivot"
    );
    let enode = peer::rpc(&client, &server.url(), "admin_nodeInfo", json!([])).await?["enode"]
        .as_str()
        .context("server enode missing")?
        .to_owned();
    let directory = tempfile::tempdir()?;
    ensure!(
        !directory.path().join("db").exists(),
        "consumer database must start empty"
    );
    for file in ["genesis.json", "l1.json", "config.json"] {
        fs::copy(
            server.directory.path().join(file),
            directory.path().join(file),
        )?;
    }
    fs::write(directory.path().join("trusted-nodes.json"), "[]")?;
    fs::write(
        directory.path().join("static-nodes.json"),
        serde_json::to_vec(&json!([enode]))?,
    )?;
    let mut args = server.args.clone();
    let old_mount = format!("{}:/data", server.directory.path().display());
    let mount = args
        .iter_mut()
        .find(|arg| **arg == old_mount)
        .context("server data mount missing")?;
    *mount = format!("{}:/data", directory.path().display());
    for (name, value) in [
        ("--Eez.SnapBootstrapEnabled", "true".to_owned()),
        ("--Sync.SynchronizationEnabled", "true".to_owned()),
        ("--Sync.FastSync", "true".to_owned()),
        ("--Sync.SnapSync", "true".to_owned()),
        ("--Sync.StaticSnapPivot", "true".to_owned()),
        ("--Sync.MaxAttemptsToUpdatePivot", "0".to_owned()),
        ("--Sync.PivotNumber", pivot_number.to_string()),
        (
            "--Sync.PivotHash",
            pivot["hash"]
                .as_str()
                .context("pivot hash missing")?
                .to_owned(),
        ),
        ("--Sync.DownloadHeadersInFastSync", "true".to_owned()),
        ("--Sync.DownloadBodiesInFastSync", "true".to_owned()),
        ("--Sync.DownloadReceiptsInFastSync", "true".to_owned()),
        ("--Sync.AncientBodiesBarrier", "1".to_owned()),
        ("--Sync.AncientReceiptsBarrier", "1".to_owned()),
        ("--Sync.SnapSyncAccountRangePartitionCount", "1".to_owned()),
        (
            "--Init.StaticNodesPath",
            "/data/static-nodes.json".to_owned(),
        ),
        ("--Init.ProcessingEnabled", "false".to_owned()),
        ("--JsonRpc.Enabled", "false".to_owned()),
    ] {
        option(&mut args, name, value);
    }
    let result_directory = Path::new(
        settings["result_directory"]
            .as_str()
            .context("result directory missing")?,
    )
    .join("snap-consumer");
    fs::create_dir_all(&result_directory)?;
    let mut consumer_settings = server.settings.clone();
    consumer_settings["database_id"] =
        json!(directory.path().file_name().unwrap().to_string_lossy());
    consumer_settings["result_directory"] = json!(result_directory);
    consumer_settings["snap_consumer"] = json!(true);
    let mut consumer = Candidate {
        directory,
        settings: consumer_settings,
        args,
        name: String::new(),
        child: None,
        port: 0,
        generation: 0,
    };
    consumer.spawn()?;
    let until = Instant::now() + Duration::from_secs(180);
    loop {
        if let Some(status) = consumer.child.as_mut().unwrap().try_wait()? {
            let log = fs::read_to_string(consumer.directory.path().join("console.log"))?;
            ensure!(status.success(), "SNAP consumer failed: {status}: {log}");
            ensure!(
                download_completed(&log),
                "consumer did not complete the actual SNAP runner: {log}"
            );
            break;
        }
        ensure!(
            Instant::now() < until,
            "SNAP consumer did not finish within 180s"
        );
        tokio::time::sleep(Duration::from_millis(200)).await;
    }
    consumer.stop();
    option(
        &mut consumer.args,
        "--Sync.SynchronizationEnabled",
        "false".to_owned(),
    );
    option(
        &mut consumer.args,
        "--Init.ProcessingEnabled",
        "true".to_owned(),
    );
    option(&mut consumer.args, "--JsonRpc.Enabled", "true".to_owned());
    consumer.launch().await?;
    let details = json!({"unsafe_transactions": settings["unsafe_transactions"]});
    consumer
        .observe(reference, 1, "before-reorg", details.clone())
        .await?;
    let advanced = peer::rpc(
        &client,
        &consumer.url(),
        "eth_getBlockByNumber",
        json!(["latest", false]),
    )
    .await?;
    let advanced_number = u64::from_str_radix(
        advanced["number"]
            .as_str()
            .context("consumer head missing")?
            .trim_start_matches("0x"),
        16,
    )?;
    ensure!(
        advanced_number > pivot_number,
        "consumer must execute native traffic after the downloaded pivot"
    );
    consumer.restart().await?;
    consumer
        .observe(reference, 1, "after-settled-restart", details)
        .await?;
    let before: Value = serde_json::from_slice(&fs::read(result_directory.join(format!(
        "{}-before-reorg.json",
        settings["case"].as_str().unwrap()
    )))?)?;
    let after: Value = serde_json::from_slice(&fs::read(result_directory.join(format!(
        "{}-after-settled-restart.json",
        settings["case"].as_str().unwrap()
    )))?)?;
    ensure!(
        before["database_id"] == after["database_id"]
            && before["database_id"] != settings["database_id"],
        "bootstrap must use one independent database across restarts"
    );
    ensure!(
        before["container"] != after["container"],
        "consumer restart must replace the process"
    );
    let result = json!({"result":"pass", "scope":"snap-bootstrap-and-follow", "candidate_revision":settings["candidate_revision"],
        "image_id":settings["image_id"], "case":settings["case"], "phase":settings["phase"], "server_database":settings["database_id"],
        "pivot":pivot, "fresh_database":true, "post_pivot_height":advanced_number, "post_pivot_transactions":post_pivot_transactions,
        "before_restart":before,"after_restart":after,"elapsed_ms":started.elapsed().as_millis()});
    fs::write(
        Path::new(settings["result_directory"].as_str().unwrap()).join("snap-bootstrap.json"),
        serde_json::to_vec_pretty(&result)?,
    )?;
    Ok(())
}

fn option(args: &mut Vec<String>, name: &str, value: String) {
    if let Some(index) = args.iter().position(|arg| arg == name) {
        args[index + 1] = value;
    } else {
        args.extend([name.to_owned(), value]);
    }
}

fn quantity(value: &Value) -> Result<u64> {
    Ok(u64::from_str_radix(
        value
            .as_str()
            .context("quantity missing")?
            .trim_start_matches("0x"),
        16,
    )?)
}

fn download_completed(log: &str) -> bool {
    let plain = without_console_colors(log);
    plain.contains("EEZ SNAP download verified.") && plain.contains("Snap sync completed.")
}

fn without_console_colors(mut text: &str) -> String {
    let mut plain = String::with_capacity(text.len());
    while let Some(start) = text.find("\x1b[") {
        plain.push_str(&text[..start]);
        let tail = &text[start + 2..];
        let end = tail.find(|c: char| !c.is_ascii_digit() && c != ';' && c != ':');
        if let Some(end) = end.filter(|&end| tail.as_bytes()[end] == b'm') {
            text = &tail[end + 1..];
        } else {
            plain.push_str("\x1b[");
            text = tail;
        }
    }
    plain.push_str(text);
    plain
}

#[cfg(test)]
mod tests {
    use super::{download_completed, without_console_colors};

    #[test]
    fn completion_requires_both_original_markers_with_or_without_console_colors() {
        for (log, expected) in [
            ("EEZ SNAP download verified.\nSnap sync completed.", true),
            (
                "EEZ SNAP download verified.\n\x1b[96mSnap\x1b[97m sync completed. \x1b[0m",
                true,
            ),
            (
                "\x1b[97mEEZ SNAP download verified.\x1b[m\nSnap sync \x1b[38;2;1;2;3mcompleted.\x1b[0m",
                true,
            ),
            ("EEZ SNAP download verified.", false),
            ("Snap sync completed.", false),
            (
                "EEZ SNAP download verified.\nSnap\x1b[broken sync completed.",
                false,
            ),
            ("", false),
        ] {
            assert_eq!(download_completed(log), expected, "completion log: {log:?}");
        }
    }

    #[test]
    fn color_normalization_preserves_other_text_and_escape_sequences() {
        for text in [
            "0m literal",
            "\u{03bb} text",
            "unfinished \x1b[",
            "unknown \x1b[2K control",
        ] {
            assert_eq!(without_console_colors(text), text);
        }
    }
}

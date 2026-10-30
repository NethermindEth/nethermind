use std::{
    fs,
    net::TcpListener,
    path::Path,
    process::{Child, Command, Stdio},
    time::{Duration, Instant},
};

use anyhow::{Context, Result, bail};
use serde_json::{Value, json};

pub struct Candidate {
    directory: tempfile::TempDir,
    settings: Value,
    args: Vec<String>,
    name: String,
    child: Option<Child>,
    port: u16,
    generation: u32,
}

impl Candidate {
    pub async fn start_cross_chain(
        case: &str,
        world: &eez_testkit::CrossChainWorld,
    ) -> Result<Self> {
        let mut env = world.follower_env();
        let (_, deploy_block) = env
            .iter_mut()
            .find(|(key, _)| *key == "EEZ_REGISTRY_DEPLOY_BLOCK")
            .context("missing registry deployment block")?;
        *deploy_block = world.dep.deploy_block.to_string();
        Self::start(case, &world.cfg.l2_genesis.0, &env).await
    }

    pub async fn start_native(case: &str, world: &eez_testkit::CrossChainWorld) -> Result<Self> {
        let mut env = world.partial_follower_env();
        let (_, block) = env
            .iter_mut()
            .find(|(name, _)| *name == "EEZ_REGISTRY_DEPLOY_BLOCK")
            .context("missing registry deployment block")?;
        *block = world.dep.deploy_block.to_string();
        env.push(("EEZ_CANDIDATE_SEQUENCER_RPC", world.l2_rpc()));
        Self::start(case, &world.cfg.l2_genesis.0, &env).await
    }

    pub async fn start(case: &str, genesis: &Path, env: &[(&str, String)]) -> Result<Self> {
        let required = |name| {
            env.iter()
                .find(|(key, _)| *key == name)
                .map(|(_, value)| value.clone())
                .with_context(|| format!("missing {name}"))
        };
        let directory = tempfile::tempdir()?;
        fs::copy(genesis, directory.path().join("genesis.json"))?;
        let image = std::env::var("EEZ_CANDIDATE_IMAGE")?;
        let user = std::env::var("EEZ_CANDIDATE_USER")?;
        let run = std::env::var("EEZ_CANDIDATE_RUN")?;
        let skip = std::env::var("EEZ_CANDIDATE_SKIP_CONSENSUS_CALLS")?;
        let interval: u64 = required("EEZ_L2_BLOCK_TIME_MS")?.parse()?;
        if interval == 0 || interval % 1000 != 0 || !matches!(skip.as_str(), "true" | "false") {
            bail!("unsupported execution timing/profile");
        }
        let profile = std::env::var("EEZ_PROFILE")?;
        let native = profile == "rollupx";
        let settings = json!({
            "profile": profile, "case": case, "l1": required("EEZ_L1_RPC_URL")?,
            "database_id": directory.path().file_name().unwrap().to_string_lossy(),
            "l1_chain_id": required("EEZ_L1_CHAIN_ID")?,
            "registry": required("EEZ_REGISTRY_ADDRESS")?, "rollup_id": required("EEZ_ROLLUP_ID")?,
            "registry_deploy_block": required("EEZ_REGISTRY_DEPLOY_BLOCK")?.parse::<u64>()?,
            "candidate_revision": std::env::var("GITHUB_SHA")?, "image_id": image,
            "result_directory": std::env::var("EEZ_EMBEDDED_RESULTS")?,
        });
        let mut args = vec![
            "run".into(),
            "--rm".into(),
            "--network=host".into(),
            "--user".into(),
            user,
            "--label".into(),
            format!("nethermind.eez.run={run}"),
            "--volume".into(),
            format!("{}:/data", directory.path().display()),
            image,
            "--Init.ChainSpecPath".into(),
            "/data/genesis.json".into(),
            "--Init.BaseDbPath".into(),
            "/data/db".into(),
            "--Init.LogDirectory".into(),
            "/data/logs".into(),
            "--JsonRpc.JwtSecretFile".into(),
            "/data/jwt".into(),
            "--KeyStore.KeyStoreDirectory".into(),
            "/data/keystore".into(),
            "--Eez.Enabled".into(),
            "true".into(),
            "--Eez.SkipConsensusLayerCalls".into(),
            skip,
            "--Eez.FollowerEnabled".into(),
            "true".into(),
            "--Eez.L1RpcUrl".into(),
            required("EEZ_L1_RPC_URL")?,
            "--Eez.L1ChainId".into(),
            required("EEZ_L1_CHAIN_ID")?,
            "--Eez.RegistryAddress".into(),
            required("EEZ_REGISTRY_ADDRESS")?,
            "--Eez.RegistryDeployBlock".into(),
            required("EEZ_REGISTRY_DEPLOY_BLOCK")?,
            "--Eez.RollupId".into(),
            required("EEZ_ROLLUP_ID")?,
            "--Eez.L1PollingIntervalMs".into(),
            "250".into(),
            "--Eez.L2BlockTimeSeconds".into(),
            (interval / 1000).to_string(),
            "--Sync.FastSync".into(),
            "false".into(),
            "--Sync.SnapSync".into(),
            "false".into(),
            "--Init.DiscoveryEnabled".into(),
            "false".into(),
            "--Pruning.Mode".into(),
            "None".into(),
            "--Receipt.TxLookupLimit".into(),
            "0".into(),
            "--JsonRpc.Enabled".into(),
            "true".into(),
            "--JsonRpc.Host".into(),
            "127.0.0.1".into(),
            "--JsonRpc.EnabledModules".into(),
            "Eth,Net,Web3,Debug,Trace".into(),
        ];
        if native {
            fs::copy(
                required("EEZ_L1_CHAIN_PATH")?,
                directory.path().join("l1.json"),
            )?;
            args.extend([
                "--Eez.L1ChainConfigPath".into(),
                "/data/l1.json".into(),
                "--Eez.L1BeaconUrl".into(),
                required("EEZ_L1_BEACON_URL")?,
                "--Eez.SequencerRpcUrl".into(),
                required("EEZ_CANDIDATE_SEQUENCER_RPC")?,
            ]);
        }
        let mut candidate = Self {
            directory,
            settings,
            args,
            name: String::new(),
            child: None,
            port: 0,
            generation: 0,
        };
        candidate.launch().await?;
        Ok(candidate)
    }

    async fn launch(&mut self) -> Result<()> {
        let rpc = TcpListener::bind("127.0.0.1:0")?;
        let p2p = TcpListener::bind("127.0.0.1:0")?;
        self.port = rpc.local_addr()?.port();
        self.generation += 1;
        self.name = format!(
            "eez-embedded-{}-{}-{}",
            std::process::id(),
            self.port,
            self.generation
        );
        let log = fs::File::create(self.directory.path().join("console.log"))?;
        let mut args = self.args.clone();
        args.splice(1..1, ["--name".into(), self.name.clone()]);
        args.extend([
            "--JsonRpc.Port".into(),
            self.port.to_string(),
            "--Network.P2PPort".into(),
            p2p.local_addr()?.port().to_string(),
        ]);
        drop((rpc, p2p));
        self.child = Some(
            Command::new("docker")
                .args(args)
                .stdin(Stdio::null())
                .stderr(log.try_clone()?)
                .stdout(log)
                .spawn()?,
        );
        let client = reqwest::Client::builder()
            .timeout(Duration::from_secs(2))
            .build()?;
        let until = Instant::now() + Duration::from_secs(120);
        while Instant::now() < until {
            if let Some(status) = self.child.as_mut().unwrap().try_wait()? {
                bail!(
                    "candidate exited before RPC readiness: {status}: {}",
                    fs::read_to_string(self.directory.path().join("console.log"))?
                );
            }
            let probe = client
                .post(self.url())
                .json(&json!({"jsonrpc":"2.0","id":1,"method":"eth_blockNumber","params":[]}))
                .send()
                .await;
            if let Ok(response) = probe {
                if let Ok(body) = response.json::<Value>().await {
                    if body["result"].as_str().is_some() {
                        return Ok(());
                    }
                }
            }
            tokio::time::sleep(Duration::from_millis(200)).await;
        }
        bail!("candidate RPC readiness timed out")
    }

    fn url(&self) -> String {
        format!("http://127.0.0.1:{}", self.port)
    }

    pub async fn verify_unsafe(
        &self,
        reference: &str,
        transactions: &Value,
        phase: &str,
    ) -> Result<()> {
        self.observe(
            reference,
            1,
            phase,
            json!({"unsafe_transactions": transactions}),
        )
        .await
    }

    pub async fn verify(&self, reference: &str, minimum: u64, phase: &str) -> Result<()> {
        self.observe(reference, minimum, phase, json!({})).await
    }

    pub async fn verify_settled(&self, reference: &str, hash: &str, phase: &str) -> Result<()> {
        self.observe(reference, 1, phase, json!({"settled_hash": hash}))
            .await
    }

    pub async fn verify_native(
        &self,
        reference: &str,
        minimum: u64,
        phase: &str,
        expected: &Value,
    ) -> Result<()> {
        self.observe(reference, minimum, phase, json!({"native_state": expected}))
            .await
    }

    pub async fn verify_unsettled(
        &self,
        reference: &str,
        transaction: &str,
        phase: &str,
    ) -> Result<()> {
        self.observe(
            reference,
            0,
            phase,
            json!({"staged_transaction": transaction}),
        )
        .await
    }

    pub async fn verify_finalized(&self, reference: &str, minimum: u64, phase: &str) -> Result<()> {
        self.observe(
            reference,
            minimum,
            phase,
            json!({"finalized_minimum": minimum}),
        )
        .await
    }

    async fn observe(
        &self,
        reference: &str,
        minimum: u64,
        phase: &str,
        details: Value,
    ) -> Result<()> {
        let mut settings = self.settings.clone();
        settings
            .as_object_mut()
            .unwrap()
            .extend(details.as_object().unwrap().clone());
        settings["reference"] = json!(reference);
        settings["candidate"] = json!(self.url());
        settings["container"] = json!(&self.name);
        settings["minimum_height"] = json!(minimum);
        settings["phase"] = json!(phase);
        let path = self.directory.path().join("verify.json");
        fs::write(&path, serde_json::to_vec(&settings)?)?;
        let script = std::env::var("EEZ_EMBEDDED_SCRIPT")?;
        let status = tokio::task::spawn_blocking(move || {
            Command::new("python3")
                .args([script.as_str(), "verify"])
                .arg(path)
                .status()
        })
        .await??;
        if !status.success() {
            bail!("candidate prefix verification failed: {status}");
        }
        Ok(())
    }

    pub async fn restart(&mut self) -> Result<()> {
        self.stop();
        self.launch().await
    }

    fn stop(&mut self) {
        if let Some(mut child) = self.child.take() {
            let _ = Command::new("docker")
                .args(["rm", "--force", &self.name])
                .stdout(Stdio::null())
                .status();
            let _ = child.wait();
            if let Ok(directory) = std::env::var("EEZ_EMBEDDED_RESULTS") {
                let _ = fs::copy(
                    self.directory.path().join("console.log"),
                    Path::new(&directory).join(format!("{}.log", self.name)),
                );
            }
        }
    }
}

impl Drop for Candidate {
    fn drop(&mut self) {
        self.stop();
    }
}

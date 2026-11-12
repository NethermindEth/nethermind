impl L1ReorgControl {
    pub async fn finalize_to(&mut self, number: u64, hash: B256) -> Result<()> {
        self.check_running()?;
        let result = self.control.path().join("finalize-result.json");
        if result.exists() {
            std::fs::remove_file(&result)?;
        }
        let temporary = self.control.path().join("finalize.new.json");
        std::fs::write(
            &temporary,
            serde_json::to_vec(&json!({"number": number, "hash": hash}))?,
        )?;
        std::fs::rename(temporary, self.control.path().join("finalize.json"))?;
        let record = wait_for(Duration::from_secs(10), || async {
            match std::fs::read(&result) {
                Ok(bytes) => Ok(Some(serde_json::from_slice::<Value>(&bytes)?)),
                Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(None),
                Err(error) => Err(error.into()),
            }
        })
        .await
        .with_context(|| format!("L1 finalization failed: {}", self.diagnostics()))?;
        self.check_running()?;
        ensure!(
            record["number"] == number && record["hash"] == json!(hash),
            "L1 finalized checkpoint identity differs"
        );
        eprintln!("Nethermind SNAP fixture finalized L1 checkpoint: {record}");
        Ok(())
    }
}

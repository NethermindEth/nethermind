impl Candidate {
    pub async fn verify_snap_bootstrap(&self, reference: &str, transactions: &Value) -> Result<()> {
        let mut settings = self.settings.clone();
        settings["phase"] = json!("after-settled-restart");
        settings["unsafe_transactions"] = transactions.clone();
        bootstrap::verify(self, reference, &settings).await
    }
}

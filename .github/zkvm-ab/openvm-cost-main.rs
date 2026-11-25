// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

use std::path::PathBuf;
use openvm_sdk::{config::{AggregationSystemParams, AppConfig}, GenericSdk, StdIn};
use openvm_sdk_config::SdkVmConfig;
use openvm_stark_sdk::config::{
    app_params_with_100_bits_security, baby_bear_poseidon2::BabyBearPoseidon2CpuEngine,
    MAX_APP_LOG_STACKED_HEIGHT,
};

mod hints;
use hints::{RunnerVmConfig, RunnerVmCpuBuilder};

type Sdk = GenericSdk<BabyBearPoseidon2CpuEngine, RunnerVmCpuBuilder>;

// One compile per ELF, then the metered-cost execution of every input, printing the retired
// instruction count and the trace cost next to the public values.
fn main() -> eyre::Result<()> {
    let a: Vec<String> = std::env::args().collect();
    if a.len() < 3 { eyre::bail!("usage: openvm-runelf-cost <elf> <input-bytes>..."); }
    let cfg = AppConfig::new(
        RunnerVmConfig(SdkVmConfig::standard()),
        app_params_with_100_bits_security(MAX_APP_LOG_STACKED_HEIGHT),
    );
    let sdk = Sdk::new(cfg, AggregationSystemParams::default())?;
    let compiled = sdk.compile_metered_cost(PathBuf::from(&a[1]))?;
    for input in &a[2..] {
        let mut stdin = StdIn::default();
        stdin.write_bytes(&std::fs::read(input)?);
        let t = std::time::Instant::now();
        let (out, (cost, instret)) = sdk.execute_metered_cost(&compiled, stdin)?;
        eprintln!("{input} executed in {:?}", t.elapsed());
        println!("RESULT {input} instret={instret} cost={cost} public_values={}", hex::encode(out));
    }
    Ok(())
}

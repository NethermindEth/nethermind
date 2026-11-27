use std::{env, error::Error, fs, path::PathBuf};

use openvm_sdk::{
    CpuSdk, StdIn,
    config::{AggregationSystemParams, AppConfig},
};
use openvm_sdk_config::SdkVmConfig;
use openvm_stark_sdk::{
    config::{MAX_APP_LOG_STACKED_HEIGHT, app_params_with_100_bits_security},
    openvm_stark_backend::p3_field::PrimeField32,
};

// ere's sdk_vm_config() and NUM_PUBLIC_VALUES_BYTES (crates/prover/openvm, crates/verifier/openvm).
const NUM_PUBLIC_VALUES_BYTES: usize = 256;

fn main() -> Result<(), Box<dyn Error>> {
    let args: Vec<String> = env::args().collect();
    let app_config = {
        let mut config = SdkVmConfig::standard();
        config.system.config = config.system.config.with_public_values_bytes(NUM_PUBLIC_VALUES_BYTES);
        AppConfig::new(config.optimize(), app_params_with_100_bits_security(MAX_APP_LOG_STACKED_HEIGHT))
    };
    let sdk = CpuSdk::builder()
        .app_config(app_config)
        .agg_params(AggregationSystemParams::default())
        .build()?;

    match args.get(1).map(String::as_str) {
        Some("run") if args.len() > 3 => {
            let compiled = sdk.compile(PathBuf::from(&args[2]))?;
            for input in &args[3..] {
                let mut stdin = StdIn::default();
                stdin.write_bytes(&fs::read(input)?);
                let start = std::time::Instant::now();
                match sdk.execute(&compiled, stdin) {
                    Ok(out) => println!("RESULT {input} {:?} public_values={}", start.elapsed(), hex::encode(out)),
                    Err(err) => println!("RESULT {input} FAILED {err:?}"),
                }
            }
        }
        Some("vk") if args.len() == 4 => {
            let commit = sdk.app_prover(fs::read(&args[2])?)?.app_exe_commit();
            let vk: Vec<u8> = commit.iter().flat_map(|element| element.as_canonical_u32().to_le_bytes()).collect();
            fs::write(&args[3], &vk)?;
            println!("vk=0x{}", hex::encode(vk));
        }
        _ => return Err("usage: run <elf> <input>... | vk <elf> <out>".into()),
    }
    Ok(())
}

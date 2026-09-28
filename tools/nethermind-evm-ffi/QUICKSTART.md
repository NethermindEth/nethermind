# Nethermind EVM for rbuilder — quick start

The short path from an empty machine to a Rust program running transactions on Nethermind's EVM.
The full walkthrough, with troubleshooting and how to read the benchmark output, is
[GUIDE.md](GUIDE.md).

Run everything **inside WSL2 (Ubuntu 22.04 or newer)** or on Linux x64. Keep the clone in the Linux
filesystem (`~/...`), not under `/mnt/c`, which is several times slower.

## 1. Prerequisites

```bash
sudo apt install -y git curl build-essential zlib1g-dev
```

The .NET SDK must be **10.0.300 or later**. Ubuntu's own package (10.0.112 on 24.04) is too old,
so install one for your user:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"
```

Rust:

```bash
curl https://sh.rustup.rs -sSf | sh -s -- -y --profile minimal
```

Put both ahead of any system SDK on your `PATH`, and add these lines to `~/.bashrc` to make it
permanent:

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$HOME/.cargo/bin:$PATH"
```

Optional, only for the allocation profile (`run.sh trace`):

```bash
dotnet tool install --global dotnet-trace
```

## 2. Get the source

```bash
git clone --depth 1 --branch feature/nethermind-evm-ffi https://github.com/NethermindEth/nethermind.git ~/nethermind
cd ~/nethermind
tools/evm-native-bench/run.sh check
```

`check` should list SDK 10.0.3xx or later, cargo, gcc and both static libraries (`libz.a`,
`libstdc++.a`).

## 3. Build the EVM bundle

```bash
tools/nethermind-evm-ffi/package.sh
```

It takes about 4 minutes the first time, while packages download, and about 25 s after that. The
last lines should say:

```
nethermind-evm 0.1.0 commit=<40-char sha> abi=1
bundle ready: ~/nethermind/tools/nethermind-evm-ffi/build/nethermind-evm-0.1.0-linux-x64
```

The bundle depends only on libc and libm, so you can copy it anywhere.

## 4. Run the tests

```bash
export NETHERMIND_EVM_DIR=~/nethermind/tools/nethermind-evm-ffi/build/nethermind-evm-0.1.0-linux-x64
cd ~/nethermind/tools/nethermind-evm-ffi/rust
cargo test --release -p nethermind-evm-sys
cargo run  --release -p nethermind-evm-smoke
```

The ABI and layout tests should report 2 passed. The smoke test should show a transfer using 21,000
gas, a contract call using 43,865 gas with one storage write and one log, a non-zero count of host
callbacks, and end with `OK`.

## 5. Benchmarks: Nethermind vs revm 38 (the version rbuilder pins)

```bash
cd ~/nethermind
tools/evm-native-bench/run.sh all
```

You can also run the pieces on their own: `run.sh nethermind`, `run.sh revm`, `run.sh publish` or
`run.sh trace`. [GUIDE.md §7.4](GUIDE.md#74-reading-the-output) explains each column. The gap
between `mean` and `p50` comes from garbage collection.

## 6. Use it from your own crate (e.g. rbuilder)

1. Add the dependency:

   ```toml
   [dependencies]
   nethermind-evm-sys = { git = "https://github.com/NethermindEth/nethermind", branch = "feature/nethermind-evm-ffi" }
   ```

   Or use a `path =` to `tools/nethermind-evm-ffi/rust/nethermind-evm-sys` in your clone.

2. **Add a `build.rs`.** Without it, your program builds but fails at start-up with
   `libnethermind_evm.so: cannot open shared object file`:

   ```rust
   fn main() {
       let lib = std::env::var("DEP_NETHERMIND_EVM_LIB").unwrap();
       println!("cargo:rustc-link-arg=-Wl,-rpath,{lib}");
   }
   ```

3. Build with `NETHERMIND_EVM_DIR` set as in step 4.

4. [rust/smoke/src/main.rs](rust/smoke/src/main.rs) is the complete worked example. It implements
   the four host callbacks, creates the engine, sets the block, executes transactions, and applies
   the returned changes in order.

## 7. Build the node from the same revision

```bash
cd ~/nethermind
dotnet build src/Nethermind/Nethermind.Runner -c release
tools/nethermind-evm-ffi/package.sh
```

At start-up, compare the bundle's `build_info()` commit with the node's `web3_clientVersion`. The
node shows only the first 8 characters of the commit, so compare prefixes. Refuse to run if the
bundle's string ends in `-dirty`.

## v1 limits

- linux-x64 and mainnet (chain id 1) only.
- No fields yet for blob hashes, authorization lists (EIP-7702) or access lists (EIP-2930), so
  those transaction types would be charged the wrong gas.

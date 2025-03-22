#!/bin/sh
set -e  # Exit on error
cargo build --release && cp target/release/libsecp256r1.so ./secp256r1.so
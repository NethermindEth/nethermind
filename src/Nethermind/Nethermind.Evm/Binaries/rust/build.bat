@echo off
cargo build --release && copy /Y target\release\secp256r1.dll .\
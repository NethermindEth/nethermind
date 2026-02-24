# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

ARTIFACTS_DIR := $(abspath ../../artifacts/bin/ZiskGuest/release)
BIN_DIR := /nethermind/bin
SRC_DIR := /nethermind/src

BFLAT_BUILD := bflat build -x --verbose \
  --arch riscv64 \
  --os linux \
  --stdlib dotnet \
  --no-pthread \
  --no-pie \
  --nostdlibrefs \
  --no-stacktrace-data \
  --no-globalization
BFLAT_REFS := \
  -r {std}/Microsoft.Win32.Primitives.dll \
  -r {std}/netstandard.dll \
  -r {std}/System.Collections.dll \
  -r {std}/System.Collections.Concurrent.dll \
  -r {std}/System.Collections.Immutable.dll \
  -r {std}/System.ComponentModel.Primitives.dll \
  -r {std}/System.Console.dll \
  -r {std}/System.Diagnostics.DiagnosticSource.dll \
  -r {std}/System.Diagnostics.Process.dll \
  -r {std}/System.Diagnostics.StackTrace.dll \
  -r {std}/System.Diagnostics.Tracing.dll \
  -r {std}/System.IO.Pipes.dll \
  -r {std}/System.Linq.dll \
  -r {std}/System.Memory.dll \
  -r {std}/System.Net.Primitives.dll \
  -r {std}/System.Net.Sockets.dll \
  -r {std}/System.Reflection.Metadata.dll \
  -r {std}/System.Runtime.dll \
  -r {std}/System.Runtime.InteropServices.dll \
  -r {std}/System.Runtime.Intrinsics.dll \
  -r {std}/System.Runtime.Numerics.dll \
  -r {std}/System.Security.Cryptography.dll \
  -r {std}/System.Text.RegularExpressions.dll \
  -r {std}/System.Threading.dll \
  -r {std}/System.Threading.Tasks.Parallel.dll \
  -r {std}/System.Threading.Thread.dll \
  -r {std}/System.Threading.ThreadPool.dll \
  -r $(BIN_DIR)/Microsoft.Extensions.ObjectPool.dll \
  -r $(BIN_DIR)/Nethermind.Abi.dll \
  -r $(BIN_DIR)/Nethermind.Blockchain.dll \
  -r $(BIN_DIR)/Nethermind.Consensus.dll \
  -r $(BIN_DIR)/Nethermind.Core.dll \
  -r $(BIN_DIR)/Nethermind.Crypto.dll \
  -r $(BIN_DIR)/Nethermind.Db.dll \
  -r $(BIN_DIR)/Nethermind.Evm.dll \
  -r $(BIN_DIR)/Nethermind.Evm.Precompiles.dll \
  -r $(BIN_DIR)/Nethermind.Facade.dll \
  -r $(BIN_DIR)/Nethermind.Int256.dll \
  -r $(BIN_DIR)/Nethermind.Logging.dll \
  -r $(BIN_DIR)/Nethermind.Serialization.Rlp.dll \
  -r $(BIN_DIR)/Nethermind.Specs.dll \
  -r $(BIN_DIR)/Nethermind.State.dll \
  -r $(BIN_DIR)/Nethermind.Stateless.Executor.dll \
  -r $(BIN_DIR)/Nethermind.Trie.dll \
  -r $(BIN_DIR)/Nethermind.TxPool.dll \
  -r $(BIN_DIR)/BouncyCastle.Cryptography.dll \
  -r $(BIN_DIR)/NonBlocking.dll \
  -r $(BIN_DIR)/FastEnum.Core.dll
BFLAT_SRC := \
  $(SRC_DIR)/Program.cs \
  $(SRC_DIR)/Zisk.Memory.cs \
  $(SRC_DIR)/Zisk.Precompiles.cs

DOCKER_RUN := docker run --platform linux/amd64 --rm \
  -e DOTNET_TYPELOADER_TRACE_INTERFACE_RESOLUTION=0 \
  -w $(SRC_DIR) \
  --mount type=bind,source="$(ARTIFACTS_DIR)",target=$(BIN_DIR) \
  --mount type=bind,source="$(CURDIR)",target=$(SRC_DIR) \
  bflat-ricsv64-local

build-docker-bflat:
	docker build --platform linux/amd64 -f Dockerfile.bflat -t bflat-ricsv64-local .

build-dotnet:
	dotnet build -c release -p:EnableZkvm=true

build-zisk: build-docker-bflat
	$(DOCKER_RUN) $(BFLAT_BUILD) --libc zisk $(BFLAT_REFS) $(BFLAT_SRC) \
    --extlib https://github.com/NethermindEth/bflat-libziskos:pre-develop-0.16.0
	mkdir -p bin; mv -f Program.patched bin/nethermind; rm -f Program Program.o

build-zisk-sim: build-docker-bflat
	$(DOCKER_RUN) $(BFLAT_BUILD) --libc zisk_sim $(BFLAT_REFS) $(BFLAT_SRC)
	mkdir -p bin; mv -f Program bin/nethermind; rm -f Program.o

run-zisk: build-zisk
	docker build --platform linux/amd64 -f Dockerfile.zisk -t zisk .
	docker run --rm -it -e RUST_BACKTRACE=full zisk ziskemu -e /n/nethermind

.PHONY: build-docker build-dotnet build-zisk build-zisk-sim run-zisk

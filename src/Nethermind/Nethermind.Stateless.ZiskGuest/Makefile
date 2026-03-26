# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

MAKEFILE_DIR := $(patsubst %/,%,$(dir $(abspath $(lastword $(MAKEFILE_LIST)))))
ARTIFACTS_DIR := $(abspath $(MAKEFILE_DIR)/../artifacts/bin)
GUEST_DIR := $(MAKEFILE_DIR)
BIN_DIR := /nethermind/bin
SRC_DIR := /nethermind/src

BFLAT_BUILD := bflat build --arch riscv64 --os linux --stdlib dotnet \
  --no-pie \
  --no-pthread \
  --no-stacktrace-data \
  --no-globalization \
  --nostdlibrefs \
  -x
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

DOCKER_RUN := docker run --platform linux/amd64 --rm \
  -e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
  -e DOTNET_TYPELOADER_TRACE_INTERFACE_RESOLUTION=0 \
  -w $(SRC_DIR) \
  --mount type=bind,source="$(ARTIFACTS_DIR)/Nethermind.Stateless.ZiskGuest/release",target=$(BIN_DIR) \
  --mount type=bind,source="$(GUEST_DIR)",target=$(SRC_DIR) \
  maximmenshikov/bflat-riscv64-zk-test21:latest \
  $(BFLAT_BUILD) \
  $(BFLAT_REFS)

build-dotnet:
	dotnet build -c release -p:EnableZkEvm=true $(GUEST_DIR)/Nethermind.Stateless.ZiskGuest.csproj

build-qemu: build-dotnet
	$(DOCKER_RUN) --libc zisk_sim -d ZK_EVM \
	$(SRC_DIR)/TestProgram.cs
	mkdir -p $(GUEST_DIR)/bin && \
		mv -f $(GUEST_DIR)/TestProgram $(GUEST_DIR)/bin/nethermind

build-zisk: build-dotnet
	$(DOCKER_RUN) \
		--libc zisk \
		-r $(BIN_DIR)/Nethermind.ZiskBindings.dll \
		--extra-ld $(BIN_DIR)/runtimes/linux-riscv64/native/libziskos.a \
		--extlib $(BIN_DIR)/libziskos.bflat.manifest \
		$(SRC_DIR)/Program.cs
	mkdir -p $(GUEST_DIR)/bin && \
		mv -f $(GUEST_DIR)/Program.patched $(GUEST_DIR)/bin/nethermind && \
		rm -f $(GUEST_DIR)/Program

run-qemu: build-qemu
	docker build --platform linux/amd64 -f $(GUEST_DIR)/Dockerfile.qemu -t nethermind-zkevm $(GUEST_DIR)
	docker run --rm -it nethermind-zkevm

run-zisk: build-zisk
	[ -f "$(GUEST_DIR)/bin/input.bin" ] || \
		dotnet run -c release --no-restore --project $(GUEST_DIR)/Nethermind.Stateless.ZiskGuest.csproj -- $(GUEST_DIR)/bin/input.bin
	docker run --rm -it -e RUST_BACKTRACE=full \
		--mount type=bind,source="$(GUEST_DIR)/bin",target=/n \
		nethermindeth/zisk:v0.16.1 \
		ziskemu -e /n/nethermind -i /n/0x1793886.bin

clean:
	rm -rf \
		$(GUEST_DIR)/bin/** \
		$(GUEST_DIR)/*.o \
		$(GUEST_DIR)/Program \
		$(GUEST_DIR)/Program.patched \
		$(GUEST_DIR)/TestProgram
	dotnet clean -c release $(MAKEFILE_DIR)/../Stateless.slnx

.PHONY: \
	build-dotnet \
	build-qemu \
	build-zisk \
	run-qemu \
	run-zisk \
	clean

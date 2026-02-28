# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

MAKEFILE_DIR := $(patsubst %/,%,$(dir $(abspath $(lastword $(MAKEFILE_LIST)))))
ARTIFACTS_DIR := $(abspath $(MAKEFILE_DIR)/../artifacts/bin)
TESTER_DIR := $(MAKEFILE_DIR)/Tester
ZISK_DIR := $(MAKEFILE_DIR)/ZiskGuest
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

DOCKER_RUN := docker run --platform linux/amd64 --rm \
  -e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
  -e DOTNET_TYPELOADER_TRACE_INTERFACE_RESOLUTION=0 \
  -w $(SRC_DIR)

build-docker-bflat:
	docker build --platform linux/amd64 -f $(MAKEFILE_DIR)/Dockerfile.bflat -t bflat-riscv64-local $(MAKEFILE_DIR)

build-dotnet-tester:
	dotnet build -c release -p:EnableZkvm=true $(TESTER_DIR)/Tester.csproj

build-dotnet-zisk:
	dotnet build -c release -p:EnableZkvm=true $(ZISK_DIR)/ZiskGuest.csproj

build-tester: build-dotnet-tester build-docker-bflat
	$(DOCKER_RUN) \
		--mount type=bind,source="$(ARTIFACTS_DIR)/Tester/release",target=$(BIN_DIR) \
		--mount type=bind,source="$(TESTER_DIR)",target=$(SRC_DIR) \
		bflat-riscv64-local \
		$(BFLAT_BUILD) --libc zisk_sim -d ZKVM $(BFLAT_REFS) \
		$(SRC_DIR)/Program.cs
	mkdir -p $(TESTER_DIR)/bin && \
		mv -f $(TESTER_DIR)/Program $(TESTER_DIR)/bin/nethermind

build-zisk: build-dotnet-zisk build-docker-bflat
	$(DOCKER_RUN) \
		--mount type=bind,source="$(ARTIFACTS_DIR)/ZiskGuest/release",target=$(BIN_DIR) \
		--mount type=bind,source="$(ZISK_DIR)",target=$(SRC_DIR) \
		bflat-riscv64-local \
		$(BFLAT_BUILD) --libc zisk $(BFLAT_REFS) \
		--extlib https://github.com/NethermindEth/bflat-libziskos:pre-develop-0.16.0 \
		$(SRC_DIR)/Program.cs \
		$(SRC_DIR)/Zisk/Crypto.cs \
		$(SRC_DIR)/Zisk/IO.cs
	mkdir -p $(ZISK_DIR)/bin && \
		mv -f $(ZISK_DIR)/Program.patched $(ZISK_DIR)/bin/nethermind && \
		rm -f $(ZISK_DIR)/Program

run-tester: build-tester
	docker build --platform linux/amd64 -t zkvm-tester $(TESTER_DIR)
	docker run --rm -it zkvm-tester

run-zisk: build-zisk
	[ -f "$(ZISK_DIR)/bin/input.bin" ] || \
		dotnet run -c release --project $(TESTER_DIR)/Tester.csproj -- $(ZISK_DIR)/bin
	docker build --platform linux/amd64 -t zisk $(ZISK_DIR)
	docker run --rm -it -e RUST_BACKTRACE=full zisk ziskemu -e /n/nethermind -i /n/input.bin

clean:
	rm -rf \
    $(TESTER_DIR)/bin/** \
		$(TESTER_DIR)/Program \
		$(TESTER_DIR)/Program.o \
		$(ZISK_DIR)/bin/** \
		$(ZISK_DIR)/Program \
		$(ZISK_DIR)/Program.patched \
		$(ZISK_DIR)/Program.o
	dotnet clean -c release $(MAKEFILE_DIR)/Stateless.slnx

.PHONY: \
	build-docker-bflat \
	build-dotnet-tester \
	build-dotnet-zisk \
	build-tester \
	build-zisk \
	run-tester \
	run-zisk \
	clean

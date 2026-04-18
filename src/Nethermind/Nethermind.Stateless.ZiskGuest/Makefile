# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

MAKEFILE_DIR := $(patsubst %/,%,$(dir $(abspath $(lastword $(MAKEFILE_LIST)))))
ARTIFACTS_DIR := $(abspath $(MAKEFILE_DIR)/../artifacts/bin)
GUEST_DIR := $(MAKEFILE_DIR)
BIN_DIR := /nethermind/bin
SRC_DIR := /nethermind/src
BFLAT_REFS := \
  -r {std}/System.Diagnostics.StackTrace.dll \
  -r {std}/System.Diagnostics.Tracing.dll \
  -r {std}/System.Memory.dll \
  -r {std}/System.Runtime.dll \
  -r $(BIN_DIR)/BouncyCastle.Cryptography.dll \
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
  -r $(BIN_DIR)/Nethermind.TxPool.dll

dotnet-build:
	dotnet build -c release -p:EnableZkEvm=true $(GUEST_DIR)/Nethermind.Stateless.ZiskGuest.csproj

build: dotnet-build
	docker run --platform linux/amd64 --rm \
		-e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
		-e DOTNET_TYPELOADER_TRACE_INTERFACE_RESOLUTION=0 \
		-w $(SRC_DIR) \
		--mount type=bind,source="$(ARTIFACTS_DIR)/Nethermind.Stateless.ZiskGuest/release",target=$(BIN_DIR) \
		--mount type=bind,source="$(GUEST_DIR)",target=$(SRC_DIR) \
		nethermindeth/bflat-riscv64:ce97bb96fb8a5c1b80d1108e5913208a856bad79 \
		bflat build --arch riscv64 --os linux --stdlib dotnet --libc zisk \
		--no-pie \
		--no-pthread \
		--no-globalization \
		--nostdlibrefs \
		$(BFLAT_REFS) \
		--extlib $(BIN_DIR)/libziskos.bflat.manifest \
		$(SRC_DIR)/Program.cs
	mkdir -p $(GUEST_DIR)/bin && \
		mv -f $(GUEST_DIR)/Program.patched $(GUEST_DIR)/bin/nethermind && \
		rm -f $(GUEST_DIR)/Program

run:
	docker run --rm -e RUST_BACKTRACE=full \
		--mount type=bind,source="$(GUEST_DIR)/bin",target=/n \
		nethermindeth/zisk:0.16.1 \
		ziskemu -e /n/nethermind -i /n/$(INPUT)

build-run: build run

clean:
	rm -rf \
		$(GUEST_DIR)/bin/** \
		$(GUEST_DIR)/Program \
		$(GUEST_DIR)/Program.o \
		$(GUEST_DIR)/Program.patched
	dotnet clean -c release $(MAKEFILE_DIR)/../Stateless.slnx

.PHONY: \
	build \
	build-run \
	dotnet-build \
	run \
	clean

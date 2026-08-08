# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

MAKEFILE_DIR := $(patsubst %/,%,$(dir $(abspath $(lastword $(MAKEFILE_LIST)))))
ARTIFACTS_DIR := $(abspath $(MAKEFILE_DIR)/../artifacts/bin)
GUEST_DIR := $(MAKEFILE_DIR)
BIN_DIR := /nethermind/bin
SRC_DIR := /nethermind/src
# Overridable so a local bflat/ziskemu build can be dropped in without editing
# the recipe. The bflat image must be one that carries the built-in zisk
# substitutions (see substitutions.zisk.xml) and understands the ISA gates below.
BFLAT_IMAGE ?= nethermindeth/bflat-riscv64:latest@sha256:6bd2c9fca626cb0d5b58621c94e4011e0ded365667d30e299002ab16965508f1
ZISK_IMAGE ?= nethermindeth/zisk:1.0.0-alpha@sha256:bad3982b10861adb452c90906ce8e93907ae6095205c89c1377a960c767b4e7a
# Fail the build here rather than in the emulator. ziskemu converts the WHOLE
# .text to ROM before executing anything, so a single F/D, compressed or atomic
# instruction - reachable or not - rejects the guest with an opaque
# "invalid riscv_instruction" panic. The IL-level error-on-float is the weakest
# of the four (it sees conv.r8/ldc.r8, but not a ceq over two float64s); the
# binary one is the exact check.
ISA_GATES ?= --error-on-float --error-on-float-binary --error-on-compressed --error-on-atomic
BFLAT_REFS := \
  -r {std}/System.Diagnostics.StackTrace.dll \
  -r {std}/System.Diagnostics.Tracing.dll \
  -r {std}/System.Memory.dll \
  -r {std}/System.Runtime.dll \
  -r {std}/System.Runtime.InteropServices.dll \
  -r $(BIN_DIR)/BouncyCastle.Cryptography.dll \
  -r $(BIN_DIR)/Microsoft.Extensions.ObjectPool.dll \
  -r $(BIN_DIR)/Nethermind.Abi.dll \
  -r $(BIN_DIR)/Nethermind.Blockchain.dll \
  -r $(BIN_DIR)/Nethermind.Config.dll \
  -r $(BIN_DIR)/Nethermind.Consensus.dll \
  -r $(BIN_DIR)/Nethermind.Core.dll \
  -r $(BIN_DIR)/Nethermind.Crypto.dll \
  -r $(BIN_DIR)/Nethermind.Db.dll \
  -r $(BIN_DIR)/Nethermind.Evm.dll \
  -r $(BIN_DIR)/Nethermind.Evm.Precompiles.dll \
  -r $(BIN_DIR)/Nethermind.Int256.dll \
  -r $(BIN_DIR)/Nethermind.Logging.dll \
  -r $(BIN_DIR)/Nethermind.Serialization.Rlp.dll \
  -r $(BIN_DIR)/Nethermind.Serialization.Ssz.dll \
  -r $(BIN_DIR)/Nethermind.Specs.dll \
  -r $(BIN_DIR)/Nethermind.State.dll \
  -r $(BIN_DIR)/Nethermind.Stateless.Executor.dll \
  -r $(BIN_DIR)/Nethermind.Trie.dll \
  -r $(BIN_DIR)/Nethermind.TxPool.dll \
  -r $(BIN_DIR)/Nethermind.Zkvm.Abstractions.dll

dotnet-build:
	dotnet build -c release -p:EnableZkEvm=true $(GUEST_DIR)/Nethermind.Stateless.ZiskGuest.csproj

build: dotnet-build
	docker run --platform linux/amd64 --rm \
		-e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
		-e DOTNET_TYPELOADER_TRACE_INTERFACE_RESOLUTION=0 \
		-w $(SRC_DIR) \
		--mount type=bind,source="$(ARTIFACTS_DIR)/Nethermind.Stateless.ZiskGuest/release",target=$(BIN_DIR) \
		--mount type=bind,source="$(GUEST_DIR)",target=$(SRC_DIR) \
		$(BFLAT_IMAGE) \
		bflat build --arch riscv64 --os linux --stdlib dotnet --libc zisk \
		--no-pie \
		--no-pthread \
		--no-globalization \
		--nostdlibrefs \
		--substitution $(SRC_DIR)/substitutions.zisk.xml \
		$(BFLAT_REFS) \
		--extlib $(BIN_DIR)/runtimes/linux-riscv64/native/libziskos.bflat.manifest \
		$(ISA_GATES) \
		$(SRC_DIR)/Program.cs
	mkdir -p $(GUEST_DIR)/bin && \
		mv -f $(GUEST_DIR)/Program.patched $(GUEST_DIR)/bin/nethermind && \
		rm -f $(GUEST_DIR)/Program

run:
	docker run --rm -e RUST_BACKTRACE=full \
		--mount type=bind,source="$(GUEST_DIR)/bin",target=/n \
		$(ZISK_IMAGE) \
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

# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

MAKEFILE_DIR := $(patsubst %/,%,$(dir $(abspath $(lastword $(MAKEFILE_LIST)))))
ARTIFACTS_DIR := $(abspath $(MAKEFILE_DIR)/../artifacts/bin)
GUEST_DIR := $(MAKEFILE_DIR)
BIN_DIR := /nethermind/bin
SRC_DIR := /nethermind/src
# The multi-VM bflat build; the ZisK image carries no sp1/openvm layout.
# TODO: pin by digest, as the ZisK guest does, once this image is tagged.
BFLAT_IMAGE ?= maximmenshikov/bflat-riscv64-multivm:latest
# Both new targets decode rv64im only, and both read the whole .text up front:
# SP1 panics on the first word it cannot decode, OpenVM turns it into a trap.
# So even unreachable F/D/C/A instructions are fatal - fail at build time instead.
ISA_GATES ?= --error-on-float --error-on-float-binary --error-on-compressed --error-on-atomic
# The guest has no instruction cache, so the code-size cost of inlining buys nothing back
# and favouring speed is close to free. Must land together with the TrieNode seqlock
# collapse: -Ot over the seqlock builds a guest whose output success flag is 0. The
# mechanism is unconfirmed, but the suspicion is that under --error-on-atomic the
# Interlocked/Volatile the seqlock was built on lower to non-atomic sequences that happen to
# work at the default optimization level and do not survive -Ot. That would not be specific
# to the seqlock: PatriciaTree.Set and Microsoft.Extensions.ObjectPool carry the same
# constructs and compile correctly today, so suspect them first if the guest ever starts
# failing silently again.
OPT_FLAGS ?= -Ot
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
	dotnet build -c release -p:EnableZkEvm=true $(GUEST_DIR)/Nethermind.Stateless.Sp1Guest.csproj

build: dotnet-build
	docker run --platform linux/amd64 --rm \
		-e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
		-e DOTNET_TYPELOADER_TRACE_INTERFACE_RESOLUTION=0 \
		-w $(SRC_DIR) \
		--mount type=bind,source="$(ARTIFACTS_DIR)/Nethermind.Stateless.Sp1Guest/release",target=$(BIN_DIR) \
		--mount type=bind,source="$(GUEST_DIR)",target=$(SRC_DIR) \
		$(BFLAT_IMAGE) \
		bflat build --arch riscv64 --os linux --stdlib dotnet --libc sp1 \
		$(OPT_FLAGS) \
		--no-pie \
		--no-pthread \
		--no-globalization \
		--nostdlibrefs \
		--substitution $(SRC_DIR)/substitutions.xml \
		$(BFLAT_REFS) \
		--extlib $(BIN_DIR)/runtimes/linux-riscv64/native/libsp1.bflat.manifest \
		$(ISA_GATES) \
		$(SRC_DIR)/Program.cs
	# No .patched step here: bflat runs patch_elf for --libc zisk only, so
	# the linked Program is the artifact.
	mkdir -p $(GUEST_DIR)/bin && \
		mv -f $(GUEST_DIR)/Program $(GUEST_DIR)/bin/nethermind

# TODO: no published SP1 executor image yet. `sp1-cli execute --elf ... --input ...`
# from the SP1 toolchain is the equivalent of ziskemu here; wire it once there is
# an image to pin, the way ZISK_IMAGE is pinned above.
run:
	@echo "SP1 execution is not wired yet - see the TODO above"; exit 1

build-run: build run

clean:
	rm -rf \
		$(GUEST_DIR)/bin/** \
		$(GUEST_DIR)/Program \
		$(GUEST_DIR)/Program.o
	dotnet clean -c release $(MAKEFILE_DIR)/../Stateless.slnx

.PHONY: \
	build \
	build-run \
	dotnet-build \
	run \
	clean

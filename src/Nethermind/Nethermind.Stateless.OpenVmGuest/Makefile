# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

MAKEFILE_DIR := $(patsubst %/,%,$(dir $(abspath $(lastword $(MAKEFILE_LIST)))))
ARTIFACTS_DIR := $(abspath $(MAKEFILE_DIR)/../artifacts/bin)
GUEST_DIR := $(MAKEFILE_DIR)
BIN_DIR := /nethermind/bin
SRC_DIR := /nethermind/src
# One image serves every guest: the bflat build carries the zisk, sp1 and
# openvm layouts side by side, so all three guests are compiled by the same
# toolchain and runtime. Pinned by digest - :latest moves with every CI run.
#
# This is the .NET 10 PERF image. The variant is not a preference: the min
# profile does not carry perf-36 (atomics as plain load/modify/store), so a
# guest built from it fails the --error-on-atomic gate below with ~240 A
# instructions, and no zkVM here implements the A extension. Between the two
# .NET lines, 10 runs ~2.6% fewer SP1 cycles and ~2.5% fewer ZisK steps than
# 11 on the nine stateless-tests mainnet blocks; 11 gives ~6.5% smaller
# binaries, which matters less than proving cost.
BFLAT_IMAGE ?= nethermindeth/bflat-riscv64:latest@sha256:ef3296763c7b8163d9f7f13de95839e03397f7a6ea9c4a6d7f902dadc843934a
# Execution harness. OpenVM publishes no image at all (cargo-openvm is a source
# install) and `cargo openvm run` only accepts a transpiled .vmexe, so this is
# our own: NethermindEth/bflat-openvm builds the openvm SDK from the
# develop-v2.1.0 rv64 line into a harness that transpiles the ELF and executes
# it (runner/ there, release-runner.yml). Pinned by digest here; OPENVM_RUNNER
# overrides it with a host binary when iterating locally.
OPENVM_IMAGE ?= nethermindeth/openvm-runner:v2.1.0-rv64@sha256:d8209a5db5d59df6d6f5899b2ce6866f81c30bdc3df952dc78243b8316706baa
OPENVM_RUNNER ?=
# OpenVM decodes rv64im only and transpiles the whole .text up front, turning
# anything it cannot decode into a trap. So even unreachable F/D/C/A
# instructions are fatal - fail at build time instead.
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
# The guest never reads the stack-trace name table or the symbol tables.
TRIM_FLAGS ?= --no-stacktrace-data --ldflags=--strip-all
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
	dotnet build -c release -p:EnableZkEvm=true $(GUEST_DIR)/Nethermind.Stateless.OpenVmGuest.csproj

build: dotnet-build
	docker run --platform linux/amd64 --rm \
		-e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
		-e DOTNET_TYPELOADER_TRACE_INTERFACE_RESOLUTION=0 \
		-w $(SRC_DIR) \
		--mount type=bind,source="$(ARTIFACTS_DIR)/Nethermind.Stateless.OpenVmGuest/release",target=$(BIN_DIR) \
		--mount type=bind,source="$(GUEST_DIR)",target=$(SRC_DIR) \
		$(BFLAT_IMAGE) \
		bflat build --arch riscv64 --os linux --stdlib dotnet --libc openvm \
		$(OPT_FLAGS) \
		$(TRIM_FLAGS) \
		--no-pie \
		--no-pthread \
		--no-globalization \
		--nostdlibrefs \
		--substitution $(SRC_DIR)/substitutions.xml \
		$(BFLAT_REFS) \
		--extlib $(BIN_DIR)/runtimes/linux-riscv64/native/libopenvm.bflat.manifest \
		--map $(SRC_DIR)/Program.map.xml \
		$(ISA_GATES) \
		$(SRC_DIR)/Program.cs
	@len=$$(sed -n 's/.*Name="__embedded_resourcedata" Length="\([0-9]*\)".*/\1/p' $(GUEST_DIR)/Program.map.xml); \
	rm -f $(GUEST_DIR)/Program.map.xml; \
	if [ -z "$$len" ]; then echo "error: could not read __embedded_resourcedata from the ILC map" >&2; exit 1; fi; \
	if [ "$$len" != "0" ]; then echo "error: the guest image embeds $$len bytes of manifest resources; the guest reads none, find the assembly that added them" >&2; exit 1; fi; \
	echo "Resource verification: OK - no manifest resources in the guest image"
	# No .patched step here: bflat runs patch_elf for --libc zisk only, so
	# the linked Program is the artifact.
	mkdir -p $(GUEST_DIR)/bin && \
		mv -f $(GUEST_DIR)/Program $(GUEST_DIR)/bin/nethermind

# SP1 and OpenVM take the SSZ payload as-is. The shared fixtures are framed for
# ZisK - an 8-byte little-endian length, the payload, then padding to 8 - so the
# frame is stripped here rather than in every guest.
# A slice would clamp silently: a truncated download, or an input that is not
# framed at all, would yield a short payload and exit 0, and the guest would
# then fail deep inside SSZ decoding - reported as a broken guest rather than a
# bad fixture. Check the length instead.
$(GUEST_DIR)/bin/%.raw: $(GUEST_DIR)/bin/%
	python3 -c "import struct,sys; b=open(sys.argv[1],'rb').read(); n=struct.unpack_from('<Q',b,0)[0]; assert 8+n <= len(b), f'framed length {n} exceeds the {len(b)}-byte file'; open(sys.argv[2],'wb').write(b[8:8+n])" $< $@

# INPUT is checked in the recipe, not as a makefile conditional: a conditional
# is evaluated while reading the file and would abort `make build` too. Without
# the check a bare `make run` asks for bin/.raw from bin/, which is a directory,
# and fails obscurely.
run:
	@[ -n "$(INPUT)" ] || { echo "error: INPUT is not set - make run INPUT=<fixture>.ssz" >&2; exit 1; }
	@$(MAKE) --no-print-directory $(GUEST_DIR)/bin/$(INPUT).raw
ifeq ($(strip $(OPENVM_RUNNER)),)
	docker run --rm -e RUST_BACKTRACE=full \
		--mount type=bind,source="$(GUEST_DIR)/bin",target=/n \
		--mount type=bind,source="$(GUEST_DIR)/openvm.toml",target=/openvm.toml,readonly \
		$(OPENVM_IMAGE) \
		/n/nethermind /openvm.toml /n/$(INPUT).raw
else
	$(OPENVM_RUNNER) $(GUEST_DIR)/bin/nethermind $(GUEST_DIR)/openvm.toml $(GUEST_DIR)/bin/$(INPUT).raw
endif

build-run: build run

clean:
	rm -rf \
		$(GUEST_DIR)/bin/*.raw \
		$(GUEST_DIR)/bin/** \
		$(GUEST_DIR)/Program \
		$(GUEST_DIR)/Program.o \
		$(GUEST_DIR)/Program.map.xml
	dotnet clean -c release $(MAKEFILE_DIR)/../Stateless.slnx

# Without this, a recipe that dies mid-write leaves its partial output on disk
# and the next run reuses it because it is newer than its prerequisite.
.DELETE_ON_ERROR:

.PHONY: \
	build \
	build-run \
	dotnet-build \
	run \
	clean

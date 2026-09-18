# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Settings shared by every zkVM guest (zisk, sp1, openvm); each guest's Makefile
# adds only what differs: the libc, the bindings manifest, the harness and the
# exit protocol. Shared rather than copied because they have to agree - cycle
# counts are comparable across targets only if the same closure is built by the
# same toolchain, and BFLAT_REFS drifting in one guest would break that silently.

# The bflat RISC-V64 compiler and .NET runtime, pinned by digest because :latest
# moves with every CI run there; the tag records the bflat-riscv64 commit.
#
# The PERF variant is required, not preferred: the min profile does not lower
# atomics to load/modify/store, so a guest built from it trips --error-on-atomic
# below with ~240 A instructions, which no zkVM here implements.
#
# .NET 11, not 10: both pass the soft-float suite, but 11 is the line it is
# maintained on. It costs ~2.6% more SP1 cycles and ~2.5% more ZisK steps over
# the nine stateless-tests blocks, and gives ~6.5% smaller binaries.
BFLAT_IMAGE ?= nethermindeth/bflat-riscv64-11:e48bd7b555baa1a592e9825240ca16ce4b012247@sha256:f5d546ee2cbf5c53790be755ef82da58031b1a323f47c0d6fc4ce1bbb1bb686a

# Every target decodes rv64im only and reads the whole .text up front, so even
# unreachable F/D/C/A instructions reject the guest - fail at build time instead.
ISA_GATES ?= --error-on-float --error-on-float-binary --error-on-compressed --error-on-atomic

# The guest has no instruction cache, so favouring speed over size is near free.
# Requires the TrieNode seqlock collapse: -Ot over the seqlock built a guest whose
# success flag was 0, unconfirmed but suspected to be the Interlocked/Volatile it
# used lowering to non-atomic sequences under --error-on-atomic. PatriciaTree.Set
# and Microsoft.Extensions.ObjectPool carry the same constructs, so suspect those
# first if the guest ever starts failing silently again.
OPT_FLAGS ?= -Ot

# The guest reads neither the stack-trace name table nor the symbol tables.
# Worth roughly 45% of the image on the sp1 and openvm guests.
TRIM_FLAGS ?= --no-stacktrace-data --ldflags=--strip-all

# Main, the ZkvmThrow export and the failure protocol are one file shared by all
# three guests; each guest's Program.cs supplies only WriteOutput. bflat compiles
# sources, not the managed assembly, so the shared file has to be mounted and
# named on its command line - the csproj <Compile Include> only covers the
# solution build.
#
# SHARED_DIR is this file's own directory, so it must stay `:=`: MAKEFILE_LIST
# ends with this file only while it is being read.
SHARED_DIR := $(patsubst %/,%,$(dir $(abspath $(lastword $(MAKEFILE_LIST)))))
SHARED_SRC_DIR := /nethermind/shared
SHARED_MOUNT := --mount type=bind,source="$(SHARED_DIR)",target=$(SHARED_SRC_DIR),readonly
GUEST_SOURCES := $(SHARED_SRC_DIR)/Program.cs $(SRC_DIR)/Program.cs
# Pinned rather than derived: with more than one source bflat names the output
# after the first, which is the shared one.
GUEST_OUT := -o $(SRC_DIR)/Program

# The managed closure. BIN_DIR is the guest's own artifacts directory, bound in
# by its build target.
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

# A recipe that dies mid-write otherwise leaves partial output that the next run
# reuses, being newer than its prerequisite.
.DELETE_ON_ERROR:

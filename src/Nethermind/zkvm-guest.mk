# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Settings shared by every zkVM guest (zisk, sp1, openvm). Included by each
# guest's Makefile, which then adds only what actually differs: the libc, the
# bindings manifest, the execution harness and the exit protocol.
#
# These are here rather than copied because they have to agree. The whole point
# of comparing cycle counts across the three targets is that the same managed
# closure is compiled by the same toolchain; if BFLAT_REFS drifts in one guest,
# it links a different set of assemblies and the comparison stops meaning
# anything. The include also keeps the image digest a one-line bump.

# bflat: the RISC-V64 compiler and the .NET runtime the guests are built from.
# Pinned by digest, with the tag recording which bflat-riscv64 commit produced
# the image - :latest moves with every CI run there.
#
# The .NET 10 PERF variant is required, not preferred. The min profile does not
# carry perf-36 (atomics as plain load/modify/store), so a guest built from it
# trips the --error-on-atomic gate below with ~240 A instructions, and no zkVM
# here implements the A extension. Between the two .NET lines, 10 runs ~2.6%
# fewer SP1 cycles and ~2.5% fewer ZisK steps over the nine stateless-tests
# mainnet blocks, while 11 gives ~6.5% smaller binaries - which matters less
# than proving cost.
BFLAT_IMAGE ?= nethermindeth/bflat-riscv64:1554355c92e0026cbb498ef1d0d7be7e53e2eebe@sha256:c0bea3f84b34be74237c513ea92d56ab9103a0cc9b28b2900a4e3a8547065832

# Every target here decodes rv64im only and reads the whole .text up front -
# ziskemu ROMs it, SP1 panics on the first word it cannot decode, OpenVM turns
# it into a trap. So even unreachable F/D/C/A instructions reject the guest:
# fail at build time instead.
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
# TRIM_FLAGS=--no-stacktrace-data keeps the symbols without changing the loaded layout.
# Worth roughly 45% of the image on the sp1 and openvm guests.
TRIM_FLAGS ?= --no-stacktrace-data --ldflags=--strip-all

# Main, the ZkvmThrow export and the failure protocol are one file shared by
# all three guests; each guest's own Program.cs supplies only WriteOutput, as
# the other half of the partial class. bflat compiles sources, not the managed
# assembly, so the shared file has to be mounted and named on its command line
# too - the csproj <Compile Include> only covers the solution build.
SHARED_DIR := $(MAKEFILE_DIR)/../Nethermind.Stateless.Guest.Shared
SHARED_SRC_DIR := /nethermind/shared
SHARED_MOUNT := --mount type=bind,source="$(SHARED_DIR)",target=$(SHARED_SRC_DIR),readonly
GUEST_SOURCES := $(SHARED_SRC_DIR)/GuestProgram.cs $(SRC_DIR)/Program.cs
# Named explicitly: with more than one source bflat takes the output name from
# the FIRST one, which would silently become GuestProgram and break every path
# downstream.
GUEST_OUT := -o $(SRC_DIR)/Program

# The managed closure. BIN_DIR is the guest's own artifacts directory, bound
# into the container by each guest's build target.
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

# A recipe that dies mid-write otherwise leaves its partial output on disk, and
# the next run reuses it because it is newer than its prerequisite.
.DELETE_ON_ERROR:

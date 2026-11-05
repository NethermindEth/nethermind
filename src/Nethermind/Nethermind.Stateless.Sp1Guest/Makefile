# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

MAKEFILE_DIR := $(patsubst %/,%,$(dir $(abspath $(lastword $(MAKEFILE_LIST)))))
ARTIFACTS_DIR := $(abspath $(MAKEFILE_DIR)/../artifacts/bin)
GUEST_DIR := $(MAKEFILE_DIR)
BIN_DIR := /nethermind/bin
SRC_DIR := /nethermind/src

GUEST_LIBC := sp1

# MUL on the uint256_mul precompile without the frame a call from managed code takes.
GUEST_OBJECTS := mul.o

# BIN_DIR must be set before the include - BFLAT_REFS expands it.
include $(MAKEFILE_DIR)/../Nethermind.Stateless.Guest/build.mk

# SP1 charges per executed instruction, so trade compile time and code size for
# fewer of them (values in hex). Tracking every local is most of the gain: the
# default 0x400 leaves locals of the largest methods out of register allocation.
OPT_FLAGS += \
	--codegenopt JitMaxLocalsToTrack=10000 \
	--codegenopt JitInlineBudget=100 \
	--codegenopt JitExtDefaultPolicyMaxIL=400 \
	--codegenopt JitExtDefaultPolicyMaxILRoot=800 \
	--codegenopt JitExtDefaultPolicyMaxBB=40 \
	--codegenopt JitCloneLoopsSizeLimit=1000 \
	--codegenopt JitLoopInversionSizeLimit=400 \
	--codegenopt 'JitOptRepeat=*' \
	--codegenopt JitOptRepeatCount=2 \
	--codegenopt JitEnableHeadTailMerge=0

# Execution harness. SP1 publishes no image that executes an arbitrary ELF, so
# this is our own: NethermindEth/bflat-sp1 wraps sp1-sdk v6.9.0 to print the
# cycle count and public values. SP1_RUNNER overrides it with a host binary.
SP1_IMAGE ?= nethermindeth/sp1-runner:v6.9.0@sha256:9f3a66911e441c3e988eeebb448f833c2d15fde3792ed963275ee6a5629dddb2
# The native executor maps guest memory through /dev/shm. Docker's 64 MB default
# fails and so does 1g; 4g is the smallest tested value that carries a mainnet
# block (~2.7 GB actual). A ceiling, not a reservation, so headroom is free.
# Too small produces an EMPTY output and a non-zero exit, which reads exactly
# like a broken guest - check this first.
SP1_SHM_SIZE ?= 8g
SP1_RUNNER ?=

run:
	@$(REQUIRE_INPUT)
	@$(MAKE) --no-print-directory $(GUEST_DIR)/bin/$(INPUT).raw
ifeq ($(strip $(SP1_RUNNER)),)
	docker run --rm --shm-size=$(SP1_SHM_SIZE) -e RUST_BACKTRACE=full \
		--mount type=bind,source="$(GUEST_DIR)/bin",target=/n \
		$(SP1_IMAGE) \
		/n/nethermind /n/$(INPUT).raw
else
	$(SP1_RUNNER) $(GUEST_DIR)/bin/nethermind $(GUEST_DIR)/bin/$(INPUT).raw
endif

# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

MAKEFILE_DIR := $(patsubst %/,%,$(dir $(abspath $(lastword $(MAKEFILE_LIST)))))
ARTIFACTS_DIR := $(abspath $(MAKEFILE_DIR)/../artifacts/bin)
GUEST_DIR := $(MAKEFILE_DIR)
BIN_DIR := /nethermind/bin
SRC_DIR := /nethermind/src

GUEST_LIBC := zisk
# The only target whose bindings library is not named after its libc.
GUEST_EXTLIB := libziskos

# BIN_DIR must be set before the include - BFLAT_REFS expands it.
include $(MAKEFILE_DIR)/../Nethermind.Stateless.Guest/build.mk

# A larger inlining budget and inlinee size limits (values in hex): code size costs
# a zkVM next to nothing. Measured flat on OpenVM, which does not set them.
OPT_FLAGS += \
	--codegenopt JitInlineBudget=100 \
	--codegenopt JitExtDefaultPolicyMaxIL=400 \
	--codegenopt JitExtDefaultPolicyMaxILRoot=800 \
	--codegenopt JitExtDefaultPolicyMaxBB=40

# Fewer executed instructions for more compile time and code size (values in hex).
# Tracking every local is most of the gain: the default 0x400 leaves locals of the
# largest methods out of register allocation.
OPT_FLAGS += \
	--codegenopt JitMaxLocalsToTrack=10000 \
	--codegenopt JitCloneLoopsSizeLimit=1000 \
	--codegenopt JitLoopInversionSizeLimit=400 \
	--codegenopt 'JitOptRepeat=*' \
	--codegenopt JitOptRepeatCount=2 \
	--codegenopt JitEnableHeadTailMerge=0

ZISK_IMAGE ?= nethermindeth/zisk:1.3.2-alpha@sha256:31a0c7f9de175765e7e81da719bdc2409060de8d23ec8683672d7ee22b798a3f

run:
	@$(REQUIRE_INPUT)
	docker run --rm -e RUST_BACKTRACE=full \
		--mount type=bind,source="$(GUEST_DIR)/bin",target=/n \
		$(ZISK_IMAGE) \
		ziskemu -e /n/nethermind -i /n/$(INPUT)

# Step count and the prover cost model for one input. Both are exact - the emulator is
# deterministic - so a single run is the measurement, with no warm-up or repetition.
stats:
	@$(REQUIRE_INPUT)
	docker run --rm -e RUST_BACKTRACE=full \
		--mount type=bind,source="$(GUEST_DIR)/bin",target=/n \
		$(ZISK_IMAGE) \
		ziskemu -e /n/nethermind -i /n/$(INPUT) -X --no-thousands-sep

.PHONY: stats

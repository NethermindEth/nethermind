# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

MAKEFILE_DIR := $(patsubst %/,%,$(dir $(abspath $(lastword $(MAKEFILE_LIST)))))
ARTIFACTS_DIR := $(abspath $(MAKEFILE_DIR)/../artifacts/bin)
GUEST_DIR := $(MAKEFILE_DIR)
BIN_DIR := /nethermind/bin
SRC_DIR := /nethermind/src

GUEST_LIBC := openvm

# MUL on the bigint extension without the frame a call from managed code takes.
GUEST_OBJECTS := mul.o

# BIN_DIR must be set before the include - BFLAT_REFS expands it.
include $(MAKEFILE_DIR)/../Nethermind.Stateless.Guest/build.mk

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

# Execution harness. OpenVM publishes no image and `cargo openvm run` only takes
# a transpiled .vmexe, so this is our own: NethermindEth/bflat-openvm builds the
# SDK's develop-v2.1.0 rv64 line into a harness that transpiles the ELF and runs
# it. `main` is still rv32 and rejects a 64-bit ELF, so the line is not a version
# preference. OPENVM_RUNNER overrides it with a host binary. The VM configuration
# is SdkVmConfig::standard() of the SDK, as in ere, so the guest has no
# openvm.toml. The image of this tag still takes a configuration argument: "-"
# selects the standard one that it ships.
OPENVM_IMAGE ?= nethermindeth/openvm-runner:v2.1.0-rv64@sha256:dc2db86864d19c749929b0665e909122afdfbbaecdab25dbf83f3b68549ac945
OPENVM_RUNNER ?=

run:
	@$(REQUIRE_INPUT)
	@$(MAKE) --no-print-directory $(GUEST_DIR)/bin/$(INPUT).raw
ifeq ($(strip $(OPENVM_RUNNER)),)
	docker run --rm -e RUST_BACKTRACE=full \
		--mount type=bind,source="$(GUEST_DIR)/bin",target=/n \
		$(OPENVM_IMAGE) \
		/n/nethermind - /n/$(INPUT).raw
else
	$(OPENVM_RUNNER) $(GUEST_DIR)/bin/nethermind $(GUEST_DIR)/bin/$(INPUT).raw
endif

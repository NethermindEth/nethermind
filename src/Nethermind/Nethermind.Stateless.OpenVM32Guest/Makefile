# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

MAKEFILE_DIR := $(patsubst %/,%,$(dir $(abspath $(lastword $(MAKEFILE_LIST)))))
ARTIFACTS_DIR := $(abspath $(MAKEFILE_DIR)/../artifacts/bin)
GUEST_DIR := $(MAKEFILE_DIR)
BIN_DIR := /nethermind/bin
SRC_DIR := /nethermind/src

# OpenVM's RV32 line (`main`). The runtime package carries this platform and
# its bindings beside the RV64 ones; --libc openvm32 selects them.
GUEST_LIBC := openvm32
GUEST_ARCH := riscv32
GUEST_PACKAGE := Nethermind.OpenVM.Runtime

# MUL on the bigint extension without the frame a call from managed code takes.
GUEST_OBJECTS := mul.o
GUEST_ASFLAGS := --target=riscv32-unknown-elf -march=rv32im -mabi=ilp32
# The bflat image with the riscv32 target, as for the Airbender guest.
BFLAT_IMAGE := nethermindeth/bflat-riscv64-11-riscv32:6f19b2372aaa412e227674255cbc11314f258028@sha256:15052345e030e87b51bc63eaf04b32626cb84a66627c1ee6aad0925fbe229a09

# WriteOutput is the RV64 guest's: both publish the Keccak-256 of the result.
OPENVM_GUEST_DIR := $(abspath $(MAKEFILE_DIR)/../Nethermind.Stateless.OpenVMGuest)
BFLAT_DOCKER_ARGS = --mount type=bind,source="$(OPENVM_GUEST_DIR)/Program.cs",target=/nethermind/openvm/Program.cs,readonly

# BIN_DIR must be set before the include - BFLAT_REFS expands it.
include $(MAKEFILE_DIR)/../Nethermind.Stateless.Guest/build.mk

GUEST_SOURCES := $(SHARED_SRC_DIR)/Program.cs $(SHARED_SRC_DIR)/Input.$(GUEST_INPUT).cs /nethermind/openvm/Program.cs

# Execution harness: runner32/ of NethermindEth/bflat-openvm, the RV32
# counterpart of the RV64 guest's runner, on SdkVmConfig::standard().
# OPENVM_RUNNER overrides it with a host binary of the same harness.
OPENVM_IMAGE ?=
OPENVM_RUNNER ?=

run:
	@$(REQUIRE_INPUT)
	@$(MAKE) --no-print-directory $(GUEST_DIR)/bin/$(INPUT).raw
ifeq ($(strip $(OPENVM_RUNNER)),)
	docker run --rm -e RUST_BACKTRACE=full \
		--mount type=bind,source="$(GUEST_DIR)/bin",target=/n \
		$(OPENVM_IMAGE) \
		/n/$(GUEST_NAME) /n/$(INPUT).raw
else
	$(OPENVM_RUNNER) $(GUEST_DIR)/bin/$(GUEST_NAME) $(GUEST_DIR)/bin/$(INPUT).raw
endif

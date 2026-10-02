# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

MAKEFILE_DIR := $(patsubst %/,%,$(dir $(abspath $(lastword $(MAKEFILE_LIST)))))
ARTIFACTS_DIR := $(abspath $(MAKEFILE_DIR)/../artifacts/bin)
GUEST_DIR := $(MAKEFILE_DIR)
BIN_DIR := /nethermind/bin
SRC_DIR := /nethermind/src

GUEST_LIBC := openvm

# BIN_DIR must be set before the include - BFLAT_REFS expands it.
include $(MAKEFILE_DIR)/../Nethermind.Stateless.Guest/build.mk
# Execution harness. OpenVM publishes no image and `cargo openvm run` only takes
# a transpiled .vmexe, so this is our own: NethermindEth/bflat-openvm builds the
# SDK's develop-v2.1.0 rv64 line into a harness that transpiles the ELF and runs
# it. `main` is still rv32 and rejects a 64-bit ELF, so the line is not a version
# preference. OPENVM_RUNNER overrides it with a host binary.
OPENVM_IMAGE ?= nethermindeth/openvm-runner:v2.1.0-rv64@sha256:d8209a5db5d59df6d6f5899b2ce6866f81c30bdc3df952dc78243b8316706baa
OPENVM_RUNNER ?=

run:
	@$(REQUIRE_INPUT)
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

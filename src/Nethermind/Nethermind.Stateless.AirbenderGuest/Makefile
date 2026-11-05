# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

MAKEFILE_DIR := $(patsubst %/,%,$(dir $(abspath $(lastword $(MAKEFILE_LIST)))))
ARTIFACTS_DIR := $(abspath $(MAKEFILE_DIR)/../artifacts/bin)
GUEST_DIR := $(MAKEFILE_DIR)
BIN_DIR := /nethermind/bin
SRC_DIR := /nethermind/src

GUEST_LIBC := airbender
GUEST_ARCH := riscv32

# The bflat image with the riscv32 target, and the Airbender platform and
# libairbender of a bflat-airbender release. bflat does not check a local
# manifest, so the download is checked against AIRBENDER_SHA256 instead.
# AIRBENDER_LIB_DIR names another libairbender build.
BFLAT_IMAGE := nethermindeth/bflat-riscv64-11-riscv32:c8f6294cca505f5118590e7fd23fdb408264f8dc@sha256:7af7b20e333de60d3e80ff371970a79164f8598ca5e02ecbb67ecbb1416eb6c7
AIRBENDER_VERSION := v1.0.3
AIRBENDER_SHA256 := 9b6bce0271fca9d4f2f0c55a1c5a28530f99f5cefab8087e33b858bf369483ec
AIRBENDER_LIB_DIR ?= $(GUEST_DIR)/obj/libairbender-$(AIRBENDER_VERSION)
BFLAT_DOCKER_ARGS = --mount type=bind,source="$(AIRBENDER_LIB_DIR)",target=/airbender,readonly
GUEST_EXTLIB := /airbender/libairbender.bflat.manifest

# Airbender's ROM is 4 MiB: favour size, and leave the reflection data out (about
# 0.9 MB). The symbols stay: bflat's post-link patch reads them.
OPT_FLAGS := -Os
TRIM_FLAGS := --no-stacktrace-data --no-reflection

# BIN_DIR must be set before the include - BFLAT_REFS expands it.
include $(MAKEFILE_DIR)/../Nethermind.Stateless.Guest/build.mk

dotnet-build: $(AIRBENDER_LIB_DIR)/libairbender.bflat.manifest

$(GUEST_DIR)/obj/libairbender-$(AIRBENDER_VERSION)/libairbender.bflat.manifest:
	mkdir -p $(@D)
	curl -fsSL -o $(@D)/libairbender-riscv32.tar.gz https://github.com/NethermindEth/bflat-airbender/releases/download/$(AIRBENDER_VERSION)/libairbender-riscv32.tar.gz
	echo "$(AIRBENDER_SHA256)  $(@D)/libairbender-riscv32.tar.gz" | sha256sum -c --quiet
	tar -xzf $(@D)/libairbender-riscv32.tar.gz -C $(@D)

ifneq ($(AIRBENDER_LIB_DIR),$(GUEST_DIR)/obj/libairbender-$(AIRBENDER_VERSION))
$(AIRBENDER_LIB_DIR)/libairbender.bflat.manifest:
	@echo "error: $(AIRBENDER_LIB_DIR) has no libairbender.bflat.manifest; AIRBENDER_LIB_DIR names the output/airbender of a bflat-airbender build" >&2; exit 1
endif

# Execution harness: the Airbender CLI's transpiler VM, from the zksync-airbender
# revision whose 4 MiB ROM and unsigned RV32M machine the image targets, as the
# image bflat-airbender publishes. It sees bin/ at the same path.
AIRBENDER_RUNNER := nethermindeth/airbender-runner:7a147da32e0c74308891a3fbd03193e724115266@sha256:024fe8c5bd052865e02da23d3a24bc17955c4543e64d15aef1f94cfc2a08eb62
AIRBENDER_CLI ?= docker run --platform linux/amd64 --rm \
	--mount type=bind,source="$(GUEST_DIR)/bin",target="$(GUEST_DIR)/bin",readonly $(AIRBENDER_RUNNER)
AIRBENDER_CYCLES ?= 12000000000

run:
	@$(REQUIRE_INPUT)
	@$(MAKE) --no-print-directory $(GUEST_DIR)/bin/$(INPUT).hex
	$(AIRBENDER_CLI) run --bin $(GUEST_DIR)/bin/$(GUEST_NAME).bin --text $(GUEST_DIR)/bin/$(GUEST_NAME).text \
		--input-file $(GUEST_DIR)/bin/$(INPUT).hex --cycles $(AIRBENDER_CYCLES)

verify:
	@$(REQUIRE_INPUT)
	@$(MAKE) --no-print-directory $(GUEST_DIR)/bin/$(INPUT).hex
	@expected=$$(python3 -c "import json,struct,sys; entries=json.load(open(sys.argv[1])); digest=next(e['digest'] for e in entries if e['input']==sys.argv[2]); print(*struct.unpack('<8I',bytes.fromhex(digest)), *([0]*8))" $(MAKEFILE_DIR)/../Nethermind.Stateless.ZiskGuest/inputs.json $(INPUT)) && \
		$(AIRBENDER_CLI) run --bin $(GUEST_DIR)/bin/$(GUEST_NAME).bin --text $(GUEST_DIR)/bin/$(GUEST_NAME).text \
		--input-file $(GUEST_DIR)/bin/$(INPUT).hex --cycles $(AIRBENDER_CYCLES) --expected-results $$expected

# Airbender reads its input as hex words: the length in bytes, then the payload in
# little-endian words, zero-padded.
$(GUEST_DIR)/bin/%.hex: $(GUEST_DIR)/bin/%.raw
	python3 -c "import struct,sys; d=open(sys.argv[1],'rb').read(); n=len(d); d+=bytes(-n % 4); w=[n, *struct.unpack(f'<{len(d)//4}I', d)]; open(sys.argv[2],'w').write(''.join(f'{x:08x}' for x in w))" $< $@

.PHONY: verify

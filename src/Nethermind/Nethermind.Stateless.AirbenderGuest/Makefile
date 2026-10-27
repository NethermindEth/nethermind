# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

MAKEFILE_DIR := $(patsubst %/,%,$(dir $(abspath $(lastword $(MAKEFILE_LIST)))))
ARTIFACTS_DIR := $(abspath $(MAKEFILE_DIR)/../artifacts/bin)
GUEST_DIR := $(MAKEFILE_DIR)
BIN_DIR := /nethermind/bin
SRC_DIR := /nethermind/src

GUEST_LIBC := airbender
GUEST_ARCH := riscv32

# No published bflat image carries the riscv32 compiler and the Airbender layout
# yet, so the guest is built by a local bflat build (BFLAT_DIR, its output
# directory) inside the bflat build image (BFLAT_IMAGE, from the bflat repository's
# Dockerfile.build), against a local libairbender build (AIRBENDER_LIB_DIR). The
# image's own ld.lld and llvm-objcopy stand in for the bundled ones, which need an
# ICU it does not have. See README.md.
BFLAT_DIR ?=
AIRBENDER_LIB_DIR ?=
BFLAT_IMAGE := bflat-riscv64
BFLAT := /bflat/bflat
BFLAT_DOCKER_ARGS = \
	--mount type=bind,source="$(BFLAT_DIR)",target=/bflat \
	--mount type=bind,source="$(AIRBENDER_LIB_DIR)",target=/airbender,readonly \
	-e BFLAT_LD=/usr/bin/ld.lld \
	-e BFLAT_OBJCOPY=/usr/bin/llvm-objcopy
GUEST_EXTLIB_MANIFEST := /airbender/libairbender.bflat.manifest

# Airbender's ROM is 4 MiB: favour size, and leave the reflection data out (about
# 0.9 MB). The symbols stay: bflat's post-link patch reads them.
OPT_FLAGS := -Os
TRIM_FLAGS := --no-stacktrace-data --no-reflection

# The bflat ISA gates read ELF64 only. Airbender decodes the whole .text when it
# loads the image and marks what it cannot run, which the run below then reports.
ISA_GATES :=

# BIN_DIR must be set before the include - BFLAT_REFS expands it.
include $(MAKEFILE_DIR)/../Nethermind.Stateless.Guest/build.mk

build: require-toolchain

require-toolchain:
	@[ -n "$(BFLAT_DIR)" ] && [ -x "$(BFLAT_DIR)/bflat" ] || { echo "error: BFLAT_DIR must name a bflat build with the Airbender layout - see README.md" >&2; exit 1; }
	@[ -f "$(AIRBENDER_LIB_DIR)/libairbender.bflat.manifest" ] || { echo "error: AIRBENDER_LIB_DIR must name a libairbender build - see README.md" >&2; exit 1; }

# Execution harness: the Airbender CLI's transpiler VM, built from the zksync-airbender
# revision whose 4 MiB ROM and unsigned RV32M machine the image targets.
AIRBENDER_CLI ?= cli
AIRBENDER_CYCLES ?= 12000000000

run:
	@$(REQUIRE_INPUT)
	@$(MAKE) --no-print-directory $(GUEST_DIR)/bin/$(INPUT).hex
	$(AIRBENDER_CLI) run --bin $(GUEST_DIR)/bin/nethermind.bin --text $(GUEST_DIR)/bin/nethermind.text \
		--input-file $(GUEST_DIR)/bin/$(INPUT).hex --cycles $(AIRBENDER_CYCLES)

verify:
	@$(REQUIRE_INPUT)
	@$(MAKE) --no-print-directory $(GUEST_DIR)/bin/$(INPUT).hex
	@expected=$$(python3 -c "import json,struct,sys; entries=json.load(open(sys.argv[1])); digest=next(e['digest'] for e in entries if e['input']==sys.argv[2]); print(*struct.unpack('<8I',bytes.fromhex(digest)), *([0]*8))" $(MAKEFILE_DIR)/../Nethermind.Stateless.ZiskGuest/inputs.json $(INPUT)) && \
		$(AIRBENDER_CLI) run --bin $(GUEST_DIR)/bin/nethermind.bin --text $(GUEST_DIR)/bin/nethermind.text \
		--input-file $(GUEST_DIR)/bin/$(INPUT).hex --cycles $(AIRBENDER_CYCLES) --expected-results $$expected

# Airbender reads its input as hex words: the length in bytes, then the payload in
# little-endian words, zero-padded.
$(GUEST_DIR)/bin/%.hex: $(GUEST_DIR)/bin/%.raw
	python3 -c "import struct,sys; d=open(sys.argv[1],'rb').read(); n=len(d); d+=bytes(-n % 4); w=[n, *struct.unpack(f'<{len(d)//4}I', d)]; open(sys.argv[2],'w').write(''.join(f'{x:08x}' for x in w))" $< $@

.PHONY: require-toolchain verify

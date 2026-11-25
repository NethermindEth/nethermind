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
BFLAT_IMAGE := nethermindeth/bflat-riscv64-11-riscv32:3e6703196d4a350e61344168b1332b77ed3e8092@sha256:4882cdff7be52c6ace87b1ee711e9d9da92f78245326bb1bf452521eb928597f
AIRBENDER_VERSION := v1.0.9
AIRBENDER_SHA256 := f5e7de3ed52ee666d6786a963df9939e01bffca4a544b1475cdf8f8e406133c6
AIRBENDER_LIB_DIR ?= $(GUEST_DIR)/obj/libairbender-$(AIRBENDER_VERSION)
BFLAT_DOCKER_ARGS = --mount type=bind,source="$(AIRBENDER_LIB_DIR)",target=/airbender,readonly
GUEST_EXTLIB := /airbender/libairbender.bflat.manifest

# Airbender's ROM is 4 MiB: favour size. The reflection data stays (about 240 KB):
# without it the type loader cannot resolve a generic virtual call the compiler
# left to run time, and the guest fails. The symbols stay too: bflat's post-link
# patch reads them.
OPT_FLAGS := -Os
TRIM_FLAGS := --no-stacktrace-data
# The compact Int256: its large arithmetic primitives stay out of line, which
# on RV32 saves hundreds of kilobytes of ROM. Its DLL has the same size and
# timestamp as the default variant's, so MSBuild would keep a default DLL left
# in the output by an earlier build; the copy is forced.
GUEST_DOTNET_FLAGS := -p:Int256ZkEvmCompact=true -p:SkipCopyUnchangedFiles=false

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
	@expected=$$(python3 -c "import json,struct,sys; digest=sys.argv[3] or next(e['digest'] for e in json.load(open(sys.argv[1])) if e['input']==sys.argv[2]); print(*struct.unpack('<8I',bytes.fromhex(digest)), *([0]*8))" $(MAKEFILE_DIR)/../Nethermind.Stateless.ZiskGuest/inputs.json $(INPUT) "$(EXPECTED_DIGEST)") && \
		$(AIRBENDER_CLI) run --bin $(GUEST_DIR)/bin/$(GUEST_NAME).bin --text $(GUEST_DIR)/bin/$(GUEST_NAME).text \
		--input-file $(GUEST_DIR)/bin/$(INPUT).hex --cycles $(AIRBENDER_CYCLES) --expected-results $$expected

# Airbender reads its input as hex words: the length in bytes, then the payload in
# little-endian words, zero-padded.
$(GUEST_DIR)/bin/%.hex: $(GUEST_DIR)/bin/%.raw
	python3 -c "import struct,sys; d=open(sys.argv[1],'rb').read(); n=len(d); d+=bytes(-n % 4); w=[n, *struct.unpack(f'<{len(d)//4}I', d)]; open(sys.argv[2],'w').write(''.join(f'{x:08x}' for x in w))" $< $@

.PHONY: verify

# Exercise the witness fallback and the division delegation against the production guest runtime.
.PHONY: test-trusted-input
test-trusted-input: dotnet-build $(GUEST_DIR)/bin/trusted-input-test.hex
	$(MAKE) -o dotnet-build build GUEST_INPUT=Trusted GUEST_NAME=trusted-input-test \
		BFLAT_DOCKER_ARGS='$(BFLAT_DOCKER_ARGS) --mount type=bind,source="$(MAKEFILE_DIR)/../Nethermind.Evm/WordDivision.zkevm.cs",target=/test/WordDivision.cs,readonly \
		--mount type=bind,source="$(MAKEFILE_DIR)/../Nethermind.Consensus/Stateless/AirbenderWitnessNodes.zkevm.cs",target=/test/WitnessNodes.cs,readonly' \
		GUEST_SOURCES='$(SRC_DIR)/tests/TrustedInput.cs /test/WordDivision.cs /test/WitnessNodes.cs'
	$(AIRBENDER_CLI) run --bin "$(GUEST_DIR)/bin/trusted-input-test.bin" --text "$(GUEST_DIR)/bin/trusted-input-test.text" \
		--input-file "$(GUEST_DIR)/bin/trusted-input-test.hex" --cycles 10000000 --expected-results 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0

$(GUEST_DIR)/bin/trusted-input-test.hex: $(MAKEFILE_DIR)/../../../scripts/ci/airbender-division-input.cs
	mkdir -p "$(@D)"
	dotnet run --file "$<" -- "$@"

# The trusted-input guest with libairbender's tries refused, so that a block runs on the managed witness nodes.
.PHONY: build-witness-fallback
build-witness-fallback: dotnet-build
	$(MAKE) -o dotnet-build build GUEST_INPUT=Trusted GUEST_NAME=nethermind-trusted-fallback \
		GUEST_SUBSTITUTIONS='$(GUEST_SUBSTITUTIONS) --substitution $(SRC_DIR)/witness-fallback.xml'

# Record and replay the same arithmetic build. INPUT is an unhinted framed .nti under bin/.
.PHONY: record-division
record-division:
	@$(REQUIRE_INPUT)
	$(MAKE) build GUEST_NAME=division-recorder GUEST_INPUT=Trusted GUEST_SUBSTITUTIONS='$(GUEST_SUBSTITUTIONS) --substitution $(SRC_DIR)/trace-division.xml'
	cd "$(MAKEFILE_DIR)/../../.." && dotnet run --file scripts/ci/record-airbender-divisions.cs -- "$(GUEST_DIR)" "$(INPUT)"

# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

MAKEFILE_DIR := $(patsubst %/,%,$(dir $(abspath $(lastword $(MAKEFILE_LIST)))))
ARTIFACTS_DIR := $(abspath $(MAKEFILE_DIR)/../artifacts/bin)
GUEST_DIR := $(MAKEFILE_DIR)
BIN_DIR := /nethermind/bin
SRC_DIR := /nethermind/src

# Toolchain image, ISA gates, optimisation/trim flags and the managed closure:
# shared by all three guests so they cannot drift apart. BIN_DIR must be set
# before the include - BFLAT_REFS expands it.
include $(MAKEFILE_DIR)/../zkvm-guest.mk
# Execution harness. SP1 publishes no image that executes an arbitrary ELF -
# ghcr.io/succinctlabs/sp1 carries the Rust toolchain for BUILDING guests and
# nothing that runs one - so this is our own: NethermindEth/bflat-sp1 builds a
# harness around sp1-sdk v6.5.0 (runner/ there, release-runner.yml) that takes
# an ELF and an input and prints the cycle count and public values. Pinned by
# digest here; SP1_RUNNER overrides it with a host binary when iterating
# locally.
SP1_IMAGE ?= nethermindeth/sp1-runner:v6.5.0@sha256:6df3e8ade2d183f7d5a6146d5deba3f76f2531677c84b6d3afef47e579b229b3
# The native executor maps guest memory through /dev/shm. Docker's default of
# 64 MB fails, and so does 1g; 4g is the smallest tested value that works on a
# mainnet block (actual usage is ~2.7 GB). This is a ceiling, not a
# reservation, so the headroom costs nothing.
#
# When it is too small the run produces an EMPTY output and a non-zero exit,
# which reads exactly like a broken guest. Check this first.
SP1_SHM_SIZE ?= 8g
SP1_RUNNER ?=

dotnet-build:
	dotnet build -c release -p:EnableZkEvm=true $(GUEST_DIR)/Nethermind.Stateless.Sp1Guest.csproj

build: dotnet-build
	docker run --platform linux/amd64 --rm \
		-e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
		-e DOTNET_TYPELOADER_TRACE_INTERFACE_RESOLUTION=0 \
		-w $(SRC_DIR) \
		--mount type=bind,source="$(ARTIFACTS_DIR)/Nethermind.Stateless.Sp1Guest/release",target=$(BIN_DIR) \
		--mount type=bind,source="$(GUEST_DIR)",target=$(SRC_DIR) \
		$(SHARED_MOUNT) \
		$(BFLAT_IMAGE) \
		bflat build --arch riscv64 --os linux --stdlib dotnet --libc sp1 \
		$(OPT_FLAGS) \
		$(TRIM_FLAGS) \
		--no-pie \
		--no-pthread \
		--no-globalization \
		--nostdlibrefs \
		--substitution $(SRC_DIR)/substitutions.xml \
		$(BFLAT_REFS) \
		--extlib $(BIN_DIR)/runtimes/linux-riscv64/native/libsp1.bflat.manifest \
		--map $(SRC_DIR)/Program.map.xml \
		$(ISA_GATES) \
		$(GUEST_OUT) \
		$(GUEST_SOURCES)
	@len=$$(sed -n 's/.*Name="__embedded_resourcedata" Length="\([0-9]*\)".*/\1/p' $(GUEST_DIR)/Program.map.xml); \
	rm -f $(GUEST_DIR)/Program.map.xml; \
	if [ -z "$$len" ]; then echo "error: could not read __embedded_resourcedata from the ILC map" >&2; exit 1; fi; \
	if [ "$$len" != "0" ]; then echo "error: the guest image embeds $$len bytes of manifest resources; the guest reads none, find the assembly that added them" >&2; exit 1; fi; \
	echo "Resource verification: OK - no manifest resources in the guest image"
	# No .patched step here: bflat runs patch_elf for --libc zisk only, so
	# the linked Program is the artifact.
	mkdir -p $(GUEST_DIR)/bin && \
		mv -f $(GUEST_DIR)/Program $(GUEST_DIR)/bin/nethermind

# SP1 and OpenVM take the SSZ payload as-is. The shared fixtures are framed for
# ZisK - an 8-byte little-endian length, the payload, then padding to 8 - so the
# frame is stripped here rather than in every guest.
# A slice would clamp silently: a truncated download, or an input that is not
# framed at all, would yield a short payload and exit 0, and the guest would
# then fail deep inside SSZ decoding - reported as a broken guest rather than a
# bad fixture. Check the length instead.
$(GUEST_DIR)/bin/%.raw: $(GUEST_DIR)/bin/%
	python3 -c "import struct,sys; b=open(sys.argv[1],'rb').read(); n=struct.unpack_from('<Q',b,0)[0]; assert 8+n <= len(b), f'framed length {n} exceeds the {len(b)}-byte file'; open(sys.argv[2],'wb').write(b[8:8+n])" $< $@

# INPUT is checked in the recipe, not as a makefile conditional: a conditional
# is evaluated while reading the file and would abort `make build` too. Without
# the check a bare `make run` asks for bin/.raw from bin/, which is a directory,
# and fails obscurely.
run:
	@[ -n "$(INPUT)" ] || { echo "error: INPUT is not set - make run INPUT=<fixture>.ssz" >&2; exit 1; }
	@$(MAKE) --no-print-directory $(GUEST_DIR)/bin/$(INPUT).raw
ifeq ($(strip $(SP1_RUNNER)),)
	docker run --rm --shm-size=$(SP1_SHM_SIZE) -e RUST_BACKTRACE=full \
		--mount type=bind,source="$(GUEST_DIR)/bin",target=/n \
		$(SP1_IMAGE) \
		/n/nethermind /n/$(INPUT).raw
else
	$(SP1_RUNNER) $(GUEST_DIR)/bin/nethermind $(GUEST_DIR)/bin/$(INPUT).raw
endif

build-run: build run

clean:
	rm -rf \
		$(GUEST_DIR)/bin/*.raw \
		$(GUEST_DIR)/bin/** \
		$(GUEST_DIR)/Program \
		$(GUEST_DIR)/Program.o \
		$(GUEST_DIR)/Program.map.xml
	dotnet clean -c release $(MAKEFILE_DIR)/../Stateless.slnx

.PHONY: \
	build \
	build-run \
	dotnet-build \
	run \
	clean

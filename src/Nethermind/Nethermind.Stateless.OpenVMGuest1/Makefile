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
# Execution harness. OpenVM publishes no image at all (cargo-openvm is a source
# install) and `cargo openvm run` only accepts a transpiled .vmexe, so this is
# our own: NethermindEth/bflat-openvm builds the openvm SDK from the
# develop-v2.1.0 rv64 line into a harness that transpiles the ELF and executes
# it (runner/ there, release-runner.yml). Pinned by digest here; OPENVM_RUNNER
# overrides it with a host binary when iterating locally.
OPENVM_IMAGE ?= nethermindeth/openvm-runner:v2.1.0-rv64@sha256:d8209a5db5d59df6d6f5899b2ce6866f81c30bdc3df952dc78243b8316706baa
OPENVM_RUNNER ?=

dotnet-build:
	dotnet build -c release -p:EnableZkEvm=true $(GUEST_DIR)/Nethermind.Stateless.OpenVMGuest.csproj

build: dotnet-build
	docker run --platform linux/amd64 --rm \
		-e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
		-e DOTNET_TYPELOADER_TRACE_INTERFACE_RESOLUTION=0 \
		-w $(SRC_DIR) \
		--mount type=bind,source="$(ARTIFACTS_DIR)/Nethermind.Stateless.OpenVMGuest/release",target=$(BIN_DIR) \
		--mount type=bind,source="$(GUEST_DIR)",target=$(SRC_DIR) \
		$(SHARED_MOUNT) \
		$(BFLAT_IMAGE) \
		bflat build --arch riscv64 --os linux --stdlib dotnet --libc openvm \
		$(OPT_FLAGS) \
		$(TRIM_FLAGS) \
		--no-pie \
		--no-pthread \
		--no-globalization \
		--nostdlibrefs \
		--substitution $(SRC_DIR)/substitutions.xml \
		$(BFLAT_REFS) \
		--extlib $(BIN_DIR)/runtimes/linux-riscv64/native/libopenvm.bflat.manifest \
		--map $(SRC_DIR)/Program.map.xml \
		$(ISA_GATES) \
		$(GUEST_OUT) \
		$(GUEST_SOURCES)
	@len=$$(sed -n 's/.*Name="__embedded_resourcedata" Length="\([0-9]*\)".*/\1/p' $(GUEST_DIR)/Program.map.xml); \
	rm -f $(GUEST_DIR)/Program.map.xml; \
	if [ -z "$$len" ]; then echo "error: could not read __embedded_resourcedata from the ILC map" >&2; exit 1; fi; \
	if [ "$$len" != "0" ]; then echo "error: the guest image embeds $$len bytes of manifest resources; the guest reads none, find the assembly that added them" >&2; exit 1; fi; \
	echo "Resource verification: OK - no manifest resources in the guest image"
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
ifeq ($(strip $(OPENVM_RUNNER)),)
	docker run --rm -e RUST_BACKTRACE=full \
		--mount type=bind,source="$(GUEST_DIR)/bin",target=/n \
		--mount type=bind,source="$(GUEST_DIR)/openvm.toml",target=/openvm.toml,readonly \
		$(OPENVM_IMAGE) \
		/n/nethermind /openvm.toml /n/$(INPUT).raw
else
	$(OPENVM_RUNNER) $(GUEST_DIR)/bin/nethermind $(GUEST_DIR)/openvm.toml $(GUEST_DIR)/bin/$(INPUT).raw
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

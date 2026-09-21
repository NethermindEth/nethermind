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

# ziskemu, the ZisK emulator.
ZISK_IMAGE ?= nethermindeth/zisk:1.2.0-alpha@sha256:7b3ea05815cf902f0bceb17458d9fbc36b34043b81370b69e57a18cbd91c2d1c

dotnet-build:
	dotnet build -c release -p:EnableZkEvm=true $(GUEST_DIR)/Nethermind.Stateless.ZiskGuest.csproj

# ILC embeds every manifest resource of every input assembly and the guest reads none, so resource
# bytes in the image are dead weight (6.9 MB of chainspecs once rode along) - fail the build instead.
build: dotnet-build
	docker run --platform linux/amd64 --rm \
		-e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
		-e DOTNET_TYPELOADER_TRACE_INTERFACE_RESOLUTION=0 \
		-w $(SRC_DIR) \
		--mount type=bind,source="$(ARTIFACTS_DIR)/Nethermind.Stateless.ZiskGuest/release",target=$(BIN_DIR) \
		--mount type=bind,source="$(GUEST_DIR)",target=$(SRC_DIR) \
		$(SHARED_MOUNT) \
		$(BFLAT_IMAGE) \
		bflat build --arch riscv64 --os linux --stdlib dotnet --libc zisk \
		$(OPT_FLAGS) \
		$(TRIM_FLAGS) \
		--no-pie \
		--no-pthread \
		--no-globalization \
		--nostdlibrefs \
		--substitution $(SRC_DIR)/substitutions.xml \
		--map $(SRC_DIR)/Program.map.xml \
		$(BFLAT_REFS) \
		--extlib $(BIN_DIR)/runtimes/linux-riscv64/native/libziskos.bflat.manifest \
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

run:
	docker run --rm -e RUST_BACKTRACE=full \
		--mount type=bind,source="$(GUEST_DIR)/bin",target=/n \
		$(ZISK_IMAGE) \
		ziskemu -e /n/nethermind -i /n/$(INPUT)

build-run: build run

clean:
	rm -rf \
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

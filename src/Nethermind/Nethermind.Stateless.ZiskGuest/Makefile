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

ZISK_IMAGE ?= nethermindeth/zisk:1.3.0-alpha@sha256:0dbfd467305a76442b06a1c2c454ed50d3b34632e70247d71139b63519cff11f

run:
	@$(REQUIRE_INPUT)
	docker run --rm -e RUST_BACKTRACE=full \
		--mount type=bind,source="$(GUEST_DIR)/bin",target=/n \
		$(ZISK_IMAGE) \
		ziskemu -e /n/nethermind -i /n/$(INPUT)

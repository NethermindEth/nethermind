# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM ubuntu:latest

RUN apt-get update && apt-get install -y --no-install-recommends \
  qemu-user-static && \
  rm -rf /var/lib/apt/lists/*

WORKDIR /n

COPY bin/ .

ENTRYPOINT ["./nethermind"]

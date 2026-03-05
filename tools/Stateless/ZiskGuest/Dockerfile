# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM rust:latest

RUN apt-get update && apt-get install -y --no-install-recommends \
  libomp-dev \
  libopenmpi-dev \
  libsodium-dev && \
  rm -rf /var/lib/apt/lists/*

# TODO: Harden Zisk installation
RUN curl -fsSL --proto '=https' --tlsv1.2 \
  https://raw.githubusercontent.com/0xPolygonHermez/zisk/b3ca745b80423c8123dc24f19039865a5bc8b074/ziskup/ziskup \
  | bash -s -- -v 0.15.0 --nokey

ENV PATH="/root/.zisk/bin:${PATH}"

WORKDIR /n

COPY bin/ .

CMD ["tail", "-f", "/dev/null"]

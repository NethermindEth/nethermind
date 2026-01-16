FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential \
    clang \
    pkg-config \
    libssl-dev \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

RUN curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y
ENV PATH="/root/.cargo/bin:${PATH}"

WORKDIR /src

COPY . .

# This will automatically trigger the 'cargo build' via the .csproj target
RUN dotnet publish src/EthProofValidator.csproj -c Release -o /app/publish

# Copy the compiled native library to the publish directory
RUN cp /src/native-zk-verifier/target/release/libnative_zk_verifier.so /app/publish/

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "EthProofValidator.dll"]


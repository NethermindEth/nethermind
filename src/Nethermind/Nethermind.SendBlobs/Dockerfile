# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM mcr.microsoft.com/dotnet/sdk:7.0-jammy AS build
COPY . .
RUN dotnet publish ./src/Nethermind/Nethermind.SendBlobs/Nethermind.SendBlobs.csproj --sc -o out


FROM mcr.microsoft.com/dotnet/runtime:7.0
RUN apt-get update && apt-get -y install libsnappy-dev libc6-dev libc6
WORKDIR /nethermind
COPY --from=build /out/Nethermind.SendBlobs .
ENTRYPOINT ["./Nethermind.SendBlobs"]

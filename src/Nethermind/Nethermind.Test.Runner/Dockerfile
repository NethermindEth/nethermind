# SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

FROM mcr.microsoft.com/dotnet/sdk:10.0-resolute AS build

COPY src/Nethermind src/Nethermind
COPY Directory.*.props .
COPY Directory.Build.targets .
COPY nuget.config .

RUN dotnet publish src/Nethermind/Nethermind.Test.Runner -c release -o /publish --sc false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-resolute

WORKDIR /nethermind

COPY --from=build /publish .

ENTRYPOINT ["./nethtest"]

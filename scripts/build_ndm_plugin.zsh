#!/bin/bash

#exit when any command fails
set -e

BIN_PATH="bin/Debug/net5.0/"
NETHERMIND_PATH="${HOME}/work/nethermind/src/Nethermind/"
NDM_PATH="${HOME}/work/ndm/src/"
NETHERMIND_RUNNER_BIN_PATH="${NETHERMIND_PATH}Nethermind.Runner/bin/Debug/net5.0/plugins/"
NDM_INFRASTRUCTURE_PATH="${NETHERMIND_PATH}Nethermind.DataMarketplace.Infrastructure/"
NDM_CHANNELS_PATH="${NETHERMIND_PATH}Nethermind.DataMarketplace.Channels/"
NDM_CHANNELS_GRPC_PATH="${NETHERMIND_PATH}Nethermind.DataMarketplace.Channels.Grpc/"
NDM_INITIALIZERS_PATH="${NETHERMIND_PATH}Nethermind.DataMarketplace.Initializers/"
NDM_PROVIDERS_PATH="${NDM_PATH}Nethermind.DataMarketplace.Providers/"
NDM_PROVIDERS_INFRASTRUCTURE_PATH="${NDM_PATH}Nethermind.DataMarketplace.Providers.Infrastructure/"
NDM_CONSUMERS_PATH="${NETHERMIND_PATH}Nethermind.DataMarketplace.Consumers/"
NDM_CONSUMERS_INFRASTRUCTURE_PATH="${NETHERMIND_PATH}Nethermind.DataMarketplace.Consumers.Infrastructure/"
NDM_REFUNDER_PATH="${NETHERMIND_PATH}Nethermind.DataMarketplace.Tools.Refunder/"
NDM_CONSUMERS_TESTS_PATH="${NETHERMIND_PATH}Nethermind.DataMarketplace.Consumers.Test/"
NDM_PROVIDERS_TESTS_PATH="${NDM_PATH}Nethermind.DataMarketplace.Providers.Test/"
NDM_TESTS_PATH="${NETHERMIND_PATH}Nethermind.DataMarketplace.Test/"

cd $NETHERMIND_PATH
dotnet build Nethermind.sln 
dotnet build DataMarketplace.sln

cd $NDM_PATH
cd ..
dotnet build 

cd $NDM_PROVIDERS_PATH
cd $BIN_PATH
cp -v ./Nethermind.DataMarketplace.Providers.{dll,pdb} $NETHERMIND_RUNNER_BIN_PATH

cd $NDM_PROVIDERS_INFRASTRUCTURE_PATH
cd $BIN_PATH
cp -v ./Nethermind.DataMarketplace.Providers.Infrastructure.{dll,pdb} $NETHERMIND_RUNNER_BIN_PATH
cp -v ./Nethermind.DataMarketplace.Providers.Plugins.{dll,pdb} $NETHERMIND_RUNNER_BIN_PATH

cd $NDM_CONSUMERS_PATH
cd $BIN_PATH
cp -v ./Nethermind.DataMarketplace.Consumers.{dll,pdb} $NETHERMIND_RUNNER_BIN_PATH

cd $NDM_CONSUMERS_INFRASTRUCTURE_PATH
cd $BIN_PATH
cp -v ./Nethermind.DataMarketplace.Consumers.Infrastructure.{dll,pdb} $NETHERMIND_RUNNER_BIN_PATH


cd $NDM_INFRASTRUCTURE_PATH
cd $BIN_PATH
cp -v ./Nethermind.DataMarketplace.Core.{dll,pdb} $NETHERMIND_RUNNER_BIN_PATH
cp -v ./Nethermind.DataMarketplace.Infrastructure.{dll,pdb} $NETHERMIND_RUNNER_BIN_PATH

cd $NDM_CHANNELS_PATH
cd $BIN_PATH
cp -v ./Nethermind.DataMarketplace.Channels.{dll,pdb} $NETHERMIND_RUNNER_BIN_PATH

cd $NDM_CHANNELS_GRPC_PATH
cd $BIN_PATH
cp -v ./Nethermind.DataMarketplace.Channels.Grpc.{dll,pdb} $NETHERMIND_RUNNER_BIN_PATH

cd $NDM_INITIALIZERS_PATH
cd $BIN_PATH
cp -v ./Nethermind.DataMarketplace.Initializers.{dll,pdb} $NETHERMIND_RUNNER_BIN_PATH
cp -v ./Nethermind.DataMarketplace.Subprotocols.{dll,pdb} $NETHERMIND_RUNNER_BIN_PATH
cp -v ./Nethermind.DataMarketplace.WebSockets.{dll,pdb} $NETHERMIND_RUNNER_BIN_PATH

cd $NDM_REFUNDER_PATH
cd $BIN_PATH
cp -v ./DnsClient.dll $NETHERMIND_RUNNER_BIN_PATH
cp -v ./MongoDB.Bson.dll $NETHERMIND_RUNNER_BIN_PATH
cp -v ./MongoDB.Driver.Core.dll $NETHERMIND_RUNNER_BIN_PATH
cp -v ./MongoDB.Driver.dll $NETHERMIND_RUNNER_BIN_PATH
cp -v ./MongoDB.Libmongocrypt.dll $NETHERMIND_RUNNER_BIN_PATH

cd $NDM_CONSUMERS_TESTS_PATH
cd $BIN_PATH
cp -v ./SharpCompress.dll $NETHERMIND_RUNNER_BIN_PATH

cd $NDM_PROVIDERS_TESTS_PATH
cd $BIN_PATH
cp -v ./YamlDotNet.dll $NETHERMIND_RUNNER_BIN_PATH

cd $NDM_TESTS_PATH
cd $BIN_PATH
cp -v ./Polly.dll $NETHERMIND_RUNNER_BIN_PATH
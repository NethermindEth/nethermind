# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

TOOLS=$HOME/.nuget/packages/grpc.tools/1.22.0/tools
PROTOC=$TOOLS/macosx_x64/protoc
PLUGIN=$TOOLS/macosx_x64/grpc_csharp_plugin
PROTO=Nethermind.proto
PROJECT=src/Nethermind/Nethermind.Grpc

$PROTOC --csharp_out $PROJECT --grpc_out $PROJECT --plugin=protoc-gen-grpc=$PLUGIN $PROJECT/$PROTO

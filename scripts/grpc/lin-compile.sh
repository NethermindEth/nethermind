TOOLS=$HOME/.nuget/packages/grpc.tools/1.22.0/tools
PROTOC=$TOOLS/linux_x64/protoc
PLUGIN=$TOOLS/linux_x64//grpc_csharp_plugin
PROTO=Nethermind.proto
PROJECT=src/Nethermind/Nethermind.Grpc

$PROTOC --csharp_out $PROJECT --grpc_out $PROJECT --plugin=protoc-gen-grpc=$PLUGIN $PROJECT/$PROTO
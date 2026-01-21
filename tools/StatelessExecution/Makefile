ROOT_DIR := $(shell pwd)
PARENT_DIR := $(shell dirname "$(ROOT_DIR)")
ARTIFACTS_DIR := $(PARENT_DIR)/artifacts/bin/StatelessExecution/release

.PHONY: all build-tool-local build-zisk-sim execute-zisk-sim build-zisk execute-zisk publish-aot run-docker

all: build-tool-local build-riscv execute-riscv
zisk_sim: build-tool-local build-zisk-sim execute-zisk-sim
zisk: build-tool-local build-zisk execute-zisk

build-tool-local:
	dotnet build -c release '-p:DefineConstants=ZKVM' .

run-docker:
	docker run -v $(ARTIFACTS_DIR):/nethermind -v $(ROOT_DIR):$(ROOT_DIR) -w $(ROOT_DIR) -it maximmenshikov/bflat-riscv64-zk  /bin/bash

build-zisk:
	./docker_build.sh bflat build --arch riscv64 --os linux -x --no-pthread --no-pie --libc zisk \
		-d ZKVM \
		--nostdlibrefs \
		--no-stacktrace-data \
		--no-globalization \
		-r nethermind/Microsoft.Extensions.ObjectPool.dll \
		-r nethermind/Nethermind.Abi.dll \
		-r nethermind/Nethermind.Blockchain.dll \
		-r nethermind/Nethermind.Consensus.dll \
		-r nethermind/Nethermind.Core.dll \
		-r nethermind/Nethermind.Crypto.dll \
		-r nethermind/Nethermind.Db.dll \
		-r nethermind/Nethermind.Evm.dll \
		-r nethermind/Nethermind.Evm.Precompiles.dll \
		-r nethermind/Nethermind.Facade.dll \
		-r nethermind/Nethermind.Int256.dll \
		-r nethermind/Nethermind.Logging.dll \
		-r nethermind/Nethermind.Serialization.Rlp.dll \
		-r nethermind/Nethermind.Specs.dll \
		-r nethermind/Nethermind.State.dll \
		-r nethermind/Nethermind.Trie.dll \
		-r nethermind/Nethermind.TxPool.dll \
		-r {std}/Microsoft.Win32.Primitives.dll \
		-r {std}/netstandard.dll \
		-r {std}/System.Collections.Concurrent.dll \
		-r {std}/System.Collections.dll \
		-r {std}/System.Collections.Immutable.dll \
		-r {std}/System.ComponentModel.Primitives.dll \
		-r {std}/System.Console.dll \
		-r {std}/System.Diagnostics.DiagnosticSource.dll \
		-r {std}/System.Diagnostics.Process.dll \
		-r {std}/System.Diagnostics.Tracing.dll \
		-r {std}/System.IO.Pipes.dll \
		-r {std}/System.Linq.dll \
		-r {std}/System.Memory.dll \
		-r {std}/System.Net.Primitives.dll \
		-r {std}/System.Net.Sockets.dll \
		-r {std}/System.Runtime.dll \
		-r {std}/System.Runtime.InteropServices.dll \
		-r {std}/System.Runtime.Intrinsics.dll \
		-r {std}/System.Runtime.Numerics.dll \
		-r {std}/System.Security.Cryptography.dll \
		-r {std}/System.Text.RegularExpressions.dll \
		-r {std}/System.Threading.dll \
		-r {std}/System.Threading.Tasks.Parallel.dll \
		-r {std}/System.Threading.Thread.dll \
		-r {std}/System.Threading.ThreadPool.dll \
		-r {std}/System.Diagnostics.StackTrace.dll \
		-r {std}/System.Reflection.Metadata.dll \
		-r nethermind/BouncyCastle.Cryptography.dll \
		-r nethermind/Nethermind.Crypto.Bls.dll \
		-r nethermind/Nethermind.Crypto.SecP256k1.dll \
		-r nethermind/Nethermind.Crypto.SecP256r1.dll \
		-r nethermind/NonBlocking.dll \
		-r nethermind/FastEnum.Core.dll \
		-r nethermind/Nethermind.GmpBindings.dll \
		-r nethermind/Nethermind.MclBindings.dll \
		-r nethermind/Ckzg.Bindings.dll \
		--stdlib dotnet --verbose ./Program.cs

build-zisk-sim:
	./docker_build.sh bflat build --arch riscv64 --os linux -x --no-pthread --no-pie --libc zisk_sim \
		-d ZKVM \
		--nostdlibrefs \
		--no-stacktrace-data \
		--no-globalization \
		-r nethermind/Microsoft.Extensions.ObjectPool.dll \
		-r nethermind/Nethermind.Abi.dll \
		-r nethermind/Nethermind.Blockchain.dll \
		-r nethermind/Nethermind.Consensus.dll \
		-r nethermind/Nethermind.Core.dll \
		-r nethermind/Nethermind.Crypto.dll \
		-r nethermind/Nethermind.Db.dll \
		-r nethermind/Nethermind.Evm.dll \
		-r nethermind/Nethermind.Evm.Precompiles.dll \
		-r nethermind/Nethermind.Facade.dll \
		-r nethermind/Nethermind.Int256.dll \
		-r nethermind/Nethermind.Logging.dll \
		-r nethermind/Nethermind.Serialization.Rlp.dll \
		-r nethermind/Nethermind.Specs.dll \
		-r nethermind/Nethermind.State.dll \
		-r nethermind/Nethermind.Trie.dll \
		-r nethermind/Nethermind.TxPool.dll \
		-r {std}/Microsoft.Win32.Primitives.dll \
		-r {std}/netstandard.dll \
		-r {std}/System.Collections.Concurrent.dll \
		-r {std}/System.Collections.dll \
		-r {std}/System.Collections.Immutable.dll \
		-r {std}/System.ComponentModel.Primitives.dll \
		-r {std}/System.Console.dll \
		-r {std}/System.Diagnostics.DiagnosticSource.dll \
		-r {std}/System.Diagnostics.Process.dll \
		-r {std}/System.Diagnostics.Tracing.dll \
		-r {std}/System.IO.Pipes.dll \
		-r {std}/System.Linq.dll \
		-r {std}/System.Memory.dll \
		-r {std}/System.Net.Primitives.dll \
		-r {std}/System.Net.Sockets.dll \
		-r {std}/System.Runtime.dll \
		-r {std}/System.Runtime.InteropServices.dll \
		-r {std}/System.Runtime.Intrinsics.dll \
		-r {std}/System.Runtime.Numerics.dll \
		-r {std}/System.Security.Cryptography.dll \
		-r {std}/System.Text.RegularExpressions.dll \
		-r {std}/System.Threading.dll \
		-r {std}/System.Threading.Tasks.Parallel.dll \
		-r {std}/System.Threading.Thread.dll \
		-r {std}/System.Threading.ThreadPool.dll \
		-r {std}/System.Diagnostics.StackTrace.dll \
		-r {std}/System.Reflection.Metadata.dll \
		-r nethermind/BouncyCastle.Cryptography.dll \
		-r nethermind/Nethermind.Crypto.Bls.dll \
		-r nethermind/Nethermind.Crypto.SecP256k1.dll \
		-r nethermind/Nethermind.Crypto.SecP256r1.dll \
		-r nethermind/NonBlocking.dll \
		-r nethermind/FastEnum.Core.dll \
		-r nethermind/Nethermind.GmpBindings.dll \
		-r nethermind/Nethermind.MclBindings.dll \
		-r nethermind/Ckzg.Bindings.dll \
		--stdlib dotnet --verbose ./Program.cs

execute-zisk-sim:
	DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 ./Program

execute-zisk:
	RUST_BACKTRACE=full ziskemu -a -e ./Program

publish-aot:
	dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishAot=true

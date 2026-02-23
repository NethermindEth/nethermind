ROOT_DIR := $(shell pwd)
PARENT_DIR := $(shell dirname "$(ROOT_DIR)")
ARTIFACTS_DIR := $(PARENT_DIR)/artifacts/bin/StatelessExecution/release

.PHONY: all build-tool-local build-zisk-sim execute-zisk-sim execute-zisk-qemu build-zisk execute-zisk publish-aot run-docker

all: build-tool-local build-zisk-sim execute-zisk-sim execute-zisk-qemu build-zisk execute-zisk
zisk-sim: build-tool-local build-zisk-sim execute-zisk-sim execute-zisk-qemu
zisk: build-tool-local build-zisk execute-zisk

build-tool-local:
	dotnet build -c release -p:EnableZkvm=true

run-docker:
	docker run -v $(ARTIFACTS_DIR):/nethermind -v $(ROOT_DIR):$(ROOT_DIR) -w $(ROOT_DIR) -it maximmenshikov/bflat-riscv64-zk  /bin/bash

build-zisk:
	rm -f Program; \
	rm -f Program.patched; \
	rm -f Program.o; \
	./docker_build.sh bflat build --arch riscv64 --os linux -x --no-pthread --no-pie --libc zisk \
		-d ZKVM \
		--nostdlibrefs \
		--no-stacktrace-data \
		--no-globalization \
		-r /nethermind/bin/Microsoft.Extensions.ObjectPool.dll \
		-r /nethermind/bin/Nethermind.Abi.dll \
		-r /nethermind/bin/Nethermind.Blockchain.dll \
		-r /nethermind/bin/Nethermind.Consensus.dll \
		-r /nethermind/bin/Nethermind.Core.dll \
		-r /nethermind/bin/Nethermind.Crypto.dll \
		-r /nethermind/bin/Nethermind.Db.dll \
		-r /nethermind/bin/Nethermind.Evm.dll \
		-r /nethermind/bin/Nethermind.Evm.Precompiles.dll \
		-r /nethermind/bin/Nethermind.Facade.dll \
		-r /nethermind/bin/Nethermind.Int256.dll \
		-r /nethermind/bin/Nethermind.Logging.dll \
		-r /nethermind/bin/Nethermind.Serialization.Rlp.dll \
		-r /nethermind/bin/Nethermind.Specs.dll \
		-r /nethermind/bin/Nethermind.State.dll \
		-r /nethermind/bin/Nethermind.Trie.dll \
		-r /nethermind/bin/Nethermind.TxPool.dll \
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
		-r /nethermind/bin/BouncyCastle.Cryptography.dll \
		-r /nethermind/bin/Nethermind.Crypto.Bls.dll \
		-r /nethermind/bin/Nethermind.Crypto.SecP256k1.dll \
		-r /nethermind/bin/Nethermind.Crypto.SecP256r1.dll \
		-r /nethermind/bin/NonBlocking.dll \
		-r /nethermind/bin/FastEnum.Core.dll \
		-r /nethermind/bin/Nethermind.GmpBindings.dll \
		-r /nethermind/bin/Nethermind.MclBindings.dll \
		-r /nethermind/bin/Ckzg.Bindings.dll \
		--stdlib dotnet --verbose /nethermind/src/Program.cs

build-zisk-sim:
	rm -f Program; \
	./docker_build.sh bflat build --arch riscv64 --os linux -x --no-pthread --no-pie --libc zisk_sim \
		-d ZKVM \
		--nostdlibrefs \
		--no-stacktrace-data \
		--no-globalization \
		-r /nethermind/bin/Microsoft.Extensions.ObjectPool.dll \
		-r /nethermind/bin/Nethermind.Abi.dll \
		-r /nethermind/bin/Nethermind.Blockchain.dll \
		-r /nethermind/bin/Nethermind.Consensus.dll \
		-r /nethermind/bin/Nethermind.Core.dll \
		-r /nethermind/bin/Nethermind.Crypto.dll \
		-r /nethermind/bin/Nethermind.Db.dll \
		-r /nethermind/bin/Nethermind.Evm.dll \
		-r /nethermind/bin/Nethermind.Evm.Precompiles.dll \
		-r /nethermind/bin/Nethermind.Facade.dll \
		-r /nethermind/bin/Nethermind.Int256.dll \
		-r /nethermind/bin/Nethermind.Logging.dll \
		-r /nethermind/bin/Nethermind.Serialization.Rlp.dll \
		-r /nethermind/bin/Nethermind.Specs.dll \
		-r /nethermind/bin/Nethermind.State.dll \
		-r /nethermind/bin/Nethermind.Trie.dll \
		-r /nethermind/bin/Nethermind.TxPool.dll \
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
		-r /nethermind/bin/BouncyCastle.Cryptography.dll \
		-r /nethermind/bin/Nethermind.Crypto.Bls.dll \
		-r /nethermind/bin/Nethermind.Crypto.SecP256k1.dll \
		-r /nethermind/bin/Nethermind.Crypto.SecP256r1.dll \
		-r /nethermind/bin/NonBlocking.dll \
		-r /nethermind/bin/FastEnum.Core.dll \
		-r /nethermind/bin/Nethermind.GmpBindings.dll \
		-r /nethermind/bin/Nethermind.MclBindings.dll \
		-r /nethermind/bin/Ckzg.Bindings.dll \
		--stdlib dotnet --verbose ./Program.cs

execute-zisk-sim:
	DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 ./Program; \
	status=$$?; \
	echo $$status; \
	exit $$status

execute-zisk-qemu:
	@port=1234; \
	qemu-riscv64 -g $$port ./Program & \
	QEMU_PID=$$!; \
	sleep 1s; \
	gdb-multiarch ./Program \
		-ex "set pagination off" \
		-ex "set style enabled on" \
		-ex "target remote :$$port" \
		-ex "break RhpThrowEx" \
		-ex "c" \
		-ex "bt" \
		-ex "detach" \
		-ex "quit" \
		--batch; \
	wait "$$QEMU_PID"

execute-zisk:
	RUST_BACKTRACE=full ziskemu -a -e ./Program.patched

publish-aot:
	dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishAot=true

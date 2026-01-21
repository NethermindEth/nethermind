ROOT_DIR := $(shell pwd)
PARENT_DIR := $(shell dirname "$(ROOT_DIR)")
ARTIFACTS_DIR := $(PARENT_DIR)/artifacts/bin/StatelessExecution/release

.PHONY: all build-tool-local build-riscv publish-aot run-docker build-zisk execute-zisk

all: build-tool-local build-riscv execute-riscv

zk: build-tool-local build-zisk execute-zisk

build-tool-local:
	dotnet build -c release -p:DefineConstants=ZKVM .

run-docker:
	docker run -v $(ARTIFACTS_DIR):/nethermind -v $(ROOT_DIR):$(ROOT_DIR) -w $(ROOT_DIR) -it maximmenshikov/bflat-riscv64-zk-test2  /bin/bash

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
                        --root-assembly Nethermind.Serialization \
			            --root-assembly Nethermind.Serialization.Rlp \
			            --root-assembly Nethermind.Logging \
						--root-type Nethermind.Serialization.Rlp.Rlp,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Logging.LimboLogs,Nethermind.Logging \
						--root-generic-method 'Nethermind.Logging.LimboLogs,Nethermind.Logging::GetClassLogger<Nethermind.Serialization.Rlp.Rlp,Nethermind.Serialization.Rlp>' \
						--root-method Nethermind.Logging.LimboLogs,Nethermind.Logging::GetClassLogger \
						--root-assembly System.Private.Reflection.Execution \
						--root-assembly System.Private.TypeLoader \
						--root-assembly System.Reflection.Metadata \
						--root-type Nethermind.Serialization.Rlp.Rlp+DecoderAttribute,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.Rlp+SkipGlobalRegistration,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.AccountDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.BlockBodyDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.BlockDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.BlockInfoDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.ChainLevelDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.CompactLogEntryDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.CompactReceiptStorageDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.HeaderDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.KeccakDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.LogEntryDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.ReceiptArrayStorageDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.ReceiptMessageDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.ReceiptStorageDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.TxDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.WithdrawalDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.Eip2930.AccessListDecoder,Nethermind.Serialization.Rlp \
						--root-type 'Nethermind.Serialization.Rlp.TxDecoders.AccessListTxDecoder`1,Nethermind.Serialization.Rlp' \
						--root-type 'Nethermind.Serialization.Rlp.TxDecoders.BlobTxDecoder`1,Nethermind.Serialization.Rlp' \
						--root-type 'Nethermind.Serialization.Rlp.TxDecoders.BaseEIP1559TxDecoder`1,Nethermind.Serialization.Rlp' \
						--root-type 'Nethermind.Serialization.Rlp.TxDecoders.EIP1559TxDecoder`1,Nethermind.Serialization.Rlp' \
						--root-type 'Nethermind.Serialization.Rlp.TxDecoders.LegacyTxDecoder`1,Nethermind.Serialization.Rlp' \
						--root-type 'Nethermind.Serialization.Rlp.TxDecoders.SetCodeTxDecoder`1,Nethermind.Serialization.Rlp' \
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
                        --root-assembly Nethermind.Serialization \
			            --root-assembly Nethermind.Serialization.Rlp \
			            --root-assembly Nethermind.Logging \
						--root-type Nethermind.Serialization.Rlp.Rlp,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Logging.LimboLogs,Nethermind.Logging \
						--root-generic-method 'Nethermind.Logging.LimboLogs,Nethermind.Logging::GetClassLogger<Nethermind.Serialization.Rlp.Rlp,Nethermind.Serialization.Rlp>' \
						--root-method Nethermind.Logging.LimboLogs,Nethermind.Logging::GetClassLogger \
						--root-assembly System.Private.Reflection.Execution \
						--root-assembly System.Private.TypeLoader \
						--root-assembly System.Reflection.Metadata \
						--root-type Nethermind.Serialization.Rlp.Rlp+DecoderAttribute,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.Rlp+SkipGlobalRegistration,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.AccountDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.BlockBodyDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.BlockDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.BlockInfoDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.ChainLevelDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.CompactLogEntryDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.CompactReceiptStorageDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.HeaderDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.KeccakDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.LogEntryDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.ReceiptArrayStorageDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.ReceiptMessageDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.ReceiptStorageDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.TxDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.WithdrawalDecoder,Nethermind.Serialization.Rlp \
						--root-type Nethermind.Serialization.Rlp.Eip2930.AccessListDecoder,Nethermind.Serialization.Rlp \
						--root-type 'Nethermind.Serialization.Rlp.TxDecoders.AccessListTxDecoder`1,Nethermind.Serialization.Rlp' \
						--root-type 'Nethermind.Serialization.Rlp.TxDecoders.BlobTxDecoder`1,Nethermind.Serialization.Rlp' \
						--root-type 'Nethermind.Serialization.Rlp.TxDecoders.BaseEIP1559TxDecoder`1,Nethermind.Serialization.Rlp' \
						--root-type 'Nethermind.Serialization.Rlp.TxDecoders.EIP1559TxDecoder`1,Nethermind.Serialization.Rlp' \
						--root-type 'Nethermind.Serialization.Rlp.TxDecoders.LegacyTxDecoder`1,Nethermind.Serialization.Rlp' \
						--root-type 'Nethermind.Serialization.Rlp.TxDecoders.SetCodeTxDecoder`1,Nethermind.Serialization.Rlp' \
                        --stdlib dotnet --verbose ./Program.cs

build-riscv:
		docker run -v $(ARTIFACTS_DIR):/nethermind -v $(ROOT_DIR):$(ROOT_DIR) -w $(ROOT_DIR) -it maximmenshikov/bflat-riscv64-zk-test3 bflat build --arch riscv64 --os linux --no-debug-info -x --no-pthread --no-pie --libc musl \
				--extra-ld {libpath}/libbrotlienc.a --extra-ld {libpath}/libbrotlicommon.a --extra-ld {libpath}/libbrotlidec.a \
				-r /nethermind/Autofac.dll \
				-r /nethermind/Autofac.Extensions.DependencyInjection.dll \
				-r /nethermind/BouncyCastle.Cryptography.dll \
				-r /nethermind/Ckzg.Bindings.dll \
				-r /nethermind/ClearScript.Core.dll \
				-r /nethermind/ClearScript.V8.dll \
				-r /nethermind/ClearScript.V8.ICUData.dll \
				-r /nethermind/ConcurrentCollections.dll \
				-r /nethermind/DotNetty.Buffers.dll \
				-r /nethermind/DotNetty.Common.dll \
				-r /nethermind/FastEnum.Core.dll \
				-r /nethermind/FSharp.Core.dll \
				-r /nethermind/MathNet.Numerics.dll \
				-r /nethermind/MathNet.Numerics.FSharp.dll \
				-r /nethermind/Microsoft.AspNetCore.Cryptography.Internal.dll \
				-r /nethermind/Microsoft.AspNetCore.DataProtection.Abstractions.dll \
				-r /nethermind/Microsoft.AspNetCore.DataProtection.dll \
				-r /nethermind/Microsoft.AspNetCore.DataProtection.Extensions.dll \
				-r /nethermind/Microsoft.Extensions.Configuration.Abstractions.dll \
				-r /nethermind/Microsoft.Extensions.DependencyInjection.Abstractions.dll \
				-r /nethermind/Microsoft.Extensions.DependencyInjection.dll \
				-r /nethermind/Microsoft.Extensions.Diagnostics.Abstractions.dll \
				-r /nethermind/Microsoft.Extensions.FileProviders.Abstractions.dll \
				-r /nethermind/Microsoft.Extensions.Hosting.Abstractions.dll \
				-r /nethermind/Microsoft.Extensions.Logging.Abstractions.dll \
				-r /nethermind/Microsoft.Extensions.Logging.dll \
				-r /nethermind/Microsoft.Extensions.ObjectPool.dll \
				-r /nethermind/Microsoft.Extensions.Options.dll \
				-r /nethermind/Microsoft.Extensions.Primitives.dll \
				-r /nethermind/Microsoft.IdentityModel.Abstractions.dll \
				-r /nethermind/Microsoft.IdentityModel.JsonWebTokens.dll \
				-r /nethermind/Microsoft.IdentityModel.Logging.dll \
				-r /nethermind/Microsoft.IdentityModel.Tokens.dll \
				-r /nethermind/Microsoft.IO.RecyclableMemoryStream.dll \
				-r /nethermind/Nethermind.Abi.dll \
				-r /nethermind/Nethermind.Blockchain.dll \
				-r /nethermind/Nethermind.Config.dll \
				-r /nethermind/Nethermind.Consensus.dll \
				-r /nethermind/Nethermind.Core.dll \
				-r /nethermind/Nethermind.Crypto.Bls.dll \
				-r /nethermind/Nethermind.Crypto.dll \
				-r /nethermind/Nethermind.Crypto.SecP256k1.dll \
				-r /nethermind/Nethermind.Crypto.SecP256r1.dll \
				-r /nethermind/Nethermind.Db.dll \
				-r /nethermind/Nethermind.Evm.dll \
				-r /nethermind/Nethermind.Evm.Precompiles.dll \
				-r /nethermind/Nethermind.Facade.dll \
				-r /nethermind/Nethermind.GmpBindings.dll \
				-r /nethermind/Nethermind.Int256.dll \
				-r /nethermind/Nethermind.Logging.dll \
				-r /nethermind/Nethermind.Logging.NLog.dll \
				-r /nethermind/Nethermind.MclBindings.dll \
				-r /nethermind/Nethermind.Network.Contract.dll \
				-r /nethermind/Nethermind.Network.Stats.dll \
				-r /nethermind/Nethermind.Serialization.Json.dll \
				-r /nethermind/Nethermind.Serialization.Rlp.dll \
				-r /nethermind/Nethermind.Specs.dll \
				-r /nethermind/Nethermind.State.dll \
				-r /nethermind/Nethermind.Synchronization.dll \
				-r /nethermind/Nethermind.Trie.dll \
				-r /nethermind/Nethermind.TxPool.dll \
				-r /nethermind/Newtonsoft.Json.dll \
				-r /nethermind/NLog.dll \
				-r /nethermind/NonBlocking.dll \
				-r /nethermind/Polly.Core.dll \
				-r /nethermind/Polly.dll \
				-r /nethermind/StatelessExecution.dll \
				-r /nethermind/System.Configuration.ConfigurationManager.dll \
				-r /nethermind/System.Diagnostics.EventLog.dll \
				-r /nethermind/System.Security.Cryptography.Pkcs.dll \
				-r /nethermind/System.Security.Cryptography.ProtectedData.dll \
				-r /nethermind/System.Security.Cryptography.Xml.dll \
				-r /nethermind/TestableIO.System.IO.Abstractions.Wrappers.dll \
				-r /nethermind/Testably.Abstractions.FileSystem.Interface.dll \
				--stdlib dotnet -O0 ./Program.cs

execute-riscv:
	DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 ./Program

execute-zisk:
	RUST_BACKTRACE=full ziskemu -a -e ./Program

publish-aot:
	dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishAot=true

# Builds libnethermind_ffi (shared and static) and the example host.
# Needs a .NET SDK with the apphost pack for the target RID, which ships nethost and the hosting headers.

DOTNET_ROOT ?= $(patsubst %/,%,$(dir $(realpath $(shell command -v dotnet))))
RID ?= $(if $(filter Darwin,$(shell uname -s)),osx,linux)-$(if $(filter x86_64,$(shell uname -m)),x64,arm64)
NETHOST_DIR ?= $(lastword $(sort $(wildcard $(DOTNET_ROOT)/packs/Microsoft.NETCore.App.Host.$(RID)/*/runtimes/$(RID)/native)))
OUT ?= build

CFLAGS ?= -O2
CFLAGS += -Wall -Wextra -fPIC -I$(NETHOST_DIR)
LDLIBS = -lstdc++ -ldl

.PHONY: all clean
all: $(OUT)/libnethermind_ffi.so $(OUT)/libnethermind_ffi.a $(OUT)/example

$(OUT)/nethermind_ffi.o: nethermind_ffi.c nethermind_ffi.h | $(OUT)
	$(CC) $(CFLAGS) -c $< -o $@

$(OUT)/libnethermind_ffi.so: $(OUT)/nethermind_ffi.o
	$(CC) -shared $< $(NETHOST_DIR)/libnethost.a $(LDLIBS) -o $@

# Consumers of the static library also link libnethost.a from NETHOST_DIR and $(LDLIBS).
$(OUT)/libnethermind_ffi.a: $(OUT)/nethermind_ffi.o
	$(AR) rcs $@ $<

$(OUT)/example: example/main.c $(OUT)/libnethermind_ffi.a
	$(CC) $(CFLAGS) $< $(OUT)/libnethermind_ffi.a $(NETHOST_DIR)/libnethost.a $(LDLIBS) -o $@

$(OUT):
	mkdir -p $@

clean:
	rm -rf $(OUT)

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#include "nethermind_ffi.h"

#include <coreclr_delegates.h>
#include <dlfcn.h>
#include <hostfxr.h>
#include <nethost.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define EXPORTS_TYPE "Nethermind.FFI.NativeExports, Nethermind.FFI"

typedef int (*start_fn)(const char*, const char* const*, int32_t);
typedef int (*wait_ready_fn)(int32_t);
typedef int (*get_head_fn)(nm_head*);
typedef int (*get_header_fn)(const uint8_t*, nm_buf*);
typedef int (*get_chain_config_fn)(nm_buf*);
typedef int (*get_code_fn)(nm_state*, const uint8_t*, nm_buf*);
typedef int (*open_state_fn)(const uint8_t*, nm_state**);
typedef void (*close_state_fn)(nm_state*);
typedef int (*get_account_fn)(nm_state*, const uint8_t*, nm_account*);
typedef int (*get_storage_fn)(nm_state*, const uint8_t*, const uint8_t*, uint8_t*);
typedef int (*get_block_hash_fn)(nm_state*, uint64_t, uint8_t*);
typedef int (*execute_block_fn)(const uint8_t*, size_t, nm_block_result*);
typedef void (*free_fn)(void*);
typedef int (*set_tx_callback_fn)(nm_tx_callback, void*);
typedef int (*set_node_callback_fn)(nm_node_callback, void*);
typedef int (*stop_fn)(int32_t);
typedef int (*join_fn)(void);

struct nm_node
{
    hostfxr_handle context;
    hostfxr_close_fn close;

    wait_ready_fn wait_ready;
    get_head_fn get_head;
    get_header_fn get_header;
    get_chain_config_fn get_chain_config;
    get_code_fn get_code;
    open_state_fn open_state;
    close_state_fn close_state;
    get_account_fn get_account;
    get_storage_fn get_storage;
    get_block_hash_fn get_block_hash;
    execute_block_fn execute_block;
    free_fn free;
    set_tx_callback_fn set_tx_callback;
    set_node_callback_fn set_node_callback;
    stop_fn stop;
    join_fn join;
};

static void set_error(char* err, size_t err_len, const char* format, ...)
{
    if (err == NULL || err_len == 0) return;

    va_list args;
    va_start(args, format);
    vsnprintf(err, err_len, format, args);
    va_end(args);
}

static int resolve(get_function_pointer_fn get_function_pointer, const char* method, void** target, char* err, size_t err_len)
{
    int rc = get_function_pointer(EXPORTS_TYPE, method, UNMANAGEDCALLERSONLY_METHOD, NULL, NULL, target);
    if (rc != 0) set_error(err, err_len, "Resolving %s failed: 0x%x", method, rc);
    return rc;
}

nm_node* nm_start(const char* ffi_dir, const char* config, int override_count, const char* const* overrides, char* err, size_t err_len)
{
    char app_path[4096];
    snprintf(app_path, sizeof app_path, "%s/Nethermind.FFI.dll", ffi_dir);

    char hostfxr_path[4096];
    size_t hostfxr_path_size = sizeof hostfxr_path;
    struct get_hostfxr_parameters hostfxr_parameters = { sizeof hostfxr_parameters, app_path, NULL };
    int rc = get_hostfxr_path(hostfxr_path, &hostfxr_path_size, &hostfxr_parameters);
    if (rc != 0)
    {
        set_error(err, err_len, "Locating hostfxr failed: 0x%x", rc);
        return NULL;
    }

    // Never closed: the runtime cannot be unloaded.
    void* hostfxr = dlopen(hostfxr_path, RTLD_NOW | RTLD_LOCAL);
    if (hostfxr == NULL)
    {
        set_error(err, err_len, "Loading %s failed: %s", hostfxr_path, dlerror());
        return NULL;
    }

    hostfxr_initialize_for_dotnet_command_line_fn initialize = (hostfxr_initialize_for_dotnet_command_line_fn)dlsym(hostfxr, "hostfxr_initialize_for_dotnet_command_line");
    hostfxr_get_runtime_delegate_fn get_delegate = (hostfxr_get_runtime_delegate_fn)dlsym(hostfxr, "hostfxr_get_runtime_delegate");
    hostfxr_close_fn close = (hostfxr_close_fn)dlsym(hostfxr, "hostfxr_close");
    if (initialize == NULL || get_delegate == NULL || close == NULL)
    {
        set_error(err, err_len, "%s does not export the hosting API", hostfxr_path);
        return NULL;
    }

    // Initialized as an app, never run: it puts every dependency from Nethermind.FFI.deps.json in the default load
    // context, where the node's plugin and type discovery look for them.
    const char* app_argv[] = { app_path };
    hostfxr_handle context = NULL;
    rc = initialize(1, app_argv, NULL, &context);
    if (rc != 0 || context == NULL)
    {
        set_error(err, err_len, "Initializing the runtime for %s failed: 0x%x", app_path, rc);
        if (context != NULL) close(context);
        return NULL;
    }

    nm_node* node = calloc(1, sizeof(nm_node));
    get_function_pointer_fn get_function_pointer = NULL;
    start_fn start = NULL;
    if (node == NULL)
    {
        set_error(err, err_len, "Out of memory");
        goto fail;
    }

    node->context = context;
    node->close = close;

    rc = get_delegate(context, hdt_get_function_pointer, (void**)&get_function_pointer);
    if (rc != 0 || get_function_pointer == NULL)
    {
        set_error(err, err_len, "Getting the function pointer delegate failed: 0x%x", rc);
        goto fail;
    }

    if (resolve(get_function_pointer, "Start", (void**)&start, err, err_len) != 0
        || resolve(get_function_pointer, "WaitReady", (void**)&node->wait_ready, err, err_len) != 0
        || resolve(get_function_pointer, "GetHead", (void**)&node->get_head, err, err_len) != 0
        || resolve(get_function_pointer, "GetHeader", (void**)&node->get_header, err, err_len) != 0
        || resolve(get_function_pointer, "GetChainConfig", (void**)&node->get_chain_config, err, err_len) != 0
        || resolve(get_function_pointer, "GetCode", (void**)&node->get_code, err, err_len) != 0
        || resolve(get_function_pointer, "OpenState", (void**)&node->open_state, err, err_len) != 0
        || resolve(get_function_pointer, "CloseState", (void**)&node->close_state, err, err_len) != 0
        || resolve(get_function_pointer, "GetAccount", (void**)&node->get_account, err, err_len) != 0
        || resolve(get_function_pointer, "GetStorage", (void**)&node->get_storage, err, err_len) != 0
        || resolve(get_function_pointer, "GetBlockHash", (void**)&node->get_block_hash, err, err_len) != 0
        || resolve(get_function_pointer, "ExecuteBlock", (void**)&node->execute_block, err, err_len) != 0
        || resolve(get_function_pointer, "Free", (void**)&node->free, err, err_len) != 0
        || resolve(get_function_pointer, "SetTxCallback", (void**)&node->set_tx_callback, err, err_len) != 0
        || resolve(get_function_pointer, "SetNodeCallback", (void**)&node->set_node_callback, err, err_len) != 0
        || resolve(get_function_pointer, "Stop", (void**)&node->stop, err, err_len) != 0
        || resolve(get_function_pointer, "Join", (void**)&node->join, err, err_len) != 0)
    {
        goto fail;
    }

    rc = start(config, overrides, override_count);
    if (rc != NM_OK)
    {
        set_error(err, err_len, "Starting the node failed: %d", rc);
        goto fail;
    }

    return node;

fail:
    close(context);
    free(node);
    return NULL;
}

int nm_wait_ready(nm_node* node, int32_t timeout_ms)
{
    return node->wait_ready(timeout_ms);
}

int nm_get_head(nm_node* node, nm_head* head)
{
    return node->get_head(head);
}

int nm_get_header(nm_node* node, const uint8_t* block_hash, nm_buf* header_rlp)
{
    return node->get_header(block_hash, header_rlp);
}

int nm_get_chain_config(nm_node* node, nm_buf* genesis_json)
{
    return node->get_chain_config(genesis_json);
}

int nm_state_open(nm_node* node, const uint8_t* block_hash, nm_state** state)
{
    return node->open_state(block_hash, state);
}

void nm_state_close(nm_node* node, nm_state* state)
{
    node->close_state(state);
}

int nm_state_get_account(nm_node* node, nm_state* state, const uint8_t* address, nm_account* account)
{
    return node->get_account(state, address, account);
}

int nm_state_get_storage(nm_node* node, nm_state* state, const uint8_t* address, const uint8_t* slot, uint8_t* value)
{
    return node->get_storage(state, address, slot, value);
}

int nm_state_get_code(nm_node* node, nm_state* state, const uint8_t* code_hash, nm_buf* code)
{
    return node->get_code(state, code_hash, code);
}

int nm_state_get_block_hash(nm_node* node, nm_state* state, uint64_t number, uint8_t* hash)
{
    return node->get_block_hash(state, number, hash);
}

void nm_free_buf(nm_node* node, nm_buf* buf)
{
    node->free(buf->data);
    memset(buf, 0, sizeof *buf);
}

int nm_execute_block(nm_node* node, const uint8_t* block_rlp, size_t block_rlp_len, nm_block_result* result)
{
    return node->execute_block(block_rlp, block_rlp_len, result);
}

void nm_free_block_result(nm_node* node, nm_block_result* result)
{
    node->free(result->receipts_rlp);
    node->free(result->error);
    memset(result, 0, sizeof *result);
}

int nm_set_tx_callback(nm_node* node, nm_tx_callback callback, void* user_data)
{
    return node->set_tx_callback(callback, user_data);
}

int nm_set_node_callback(nm_node* node, nm_node_callback callback, void* user_data)
{
    return node->set_node_callback(callback, user_data);
}

int nm_stop(nm_node* node, int32_t exit_code)
{
    return node->stop(exit_code);
}

int nm_join(nm_node* node)
{
    int exit_code = node->join();
    node->close(node->context);
    free(node);
    return exit_code;
}

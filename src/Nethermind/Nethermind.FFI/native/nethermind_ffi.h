// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

/*
 * Hosts a Nethermind node in-process and exposes it to C (and anything that can bind to C, e.g. Rust).
 *
 * The node runs in-process from the Nethermind.FFI build output, configured like the executable's --config plus
 * --Category.Name overrides. Only one node per process is supported: the .NET runtime cannot be unloaded or started
 * twice.
 *
 * Known limitations:
 *  - Block validation uses the L1 Ethereum validator; chains with their own (Optimism, Taiko, Xdc) are not supported.
 *  - The node may terminate the whole process (Environment.Exit on fatal database errors). Signal handling is left
 *    to the host; call nm_stop to shut down.
 *  - Linux and macOS only.
 */

#ifndef NETHERMIND_FFI_H
#define NETHERMIND_FFI_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef enum nm_status
{
    NM_OK = 0,
    /* The node has not finished initializing yet. */
    NM_NOT_READY = 1,
    /* The node is shutting down or has stopped. */
    NM_STOPPED = 2,
    /* The block RLP could not be decoded. */
    NM_DECODE_ERROR = 3,
    /* The block's parent is not in the block tree. */
    NM_PARENT_NOT_FOUND = 4,
    /* The parent's state is not available (pruned or not synced). */
    NM_STATE_UNAVAILABLE = 5,
    /* The block failed validation; see nm_block_result.error. */
    NM_INVALID_BLOCK = 6,
    /* An unexpected managed exception; see nm_block_result.error where available. */
    NM_INTERNAL_ERROR = 7,
    /* A tx callback is already set; clear it with NULL first. */
    NM_CALLBACK_ALREADY_SET = 8,
    /* The requested block, account or code does not exist. */
    NM_NOT_FOUND = 9,
} nm_status;

typedef struct nm_head
{
    uint64_t number;
    uint8_t hash[32];
    uint8_t state_root[32];
} nm_head;

/* Release with nm_free_block_result. */
typedef struct nm_block_result
{
    uint64_t gas_used;
    uint8_t state_root[32];
    uint8_t receipts_root[32];
    uint8_t block_hash[32];
    /* RLP list of the receipts in eth wire form; set when the status is NM_OK. */
    uint8_t* receipts_rlp;
    size_t receipts_len;
    /* UTF-8, not NUL-terminated; set when the status is an error that carries a message. */
    uint8_t* error;
    size_t error_len;
} nm_block_result;

/* Bytes owned by the caller; release with nm_free_buf. */
typedef struct nm_buf
{
    uint8_t* data;
    size_t len;
} nm_buf;

typedef struct nm_account
{
    uint64_t nonce;
    /* Big-endian. */
    uint8_t balance[32];
    uint8_t code_hash[32];
} nm_account;

typedef enum nm_tx_event
{
    /* The tx was added to the pool. */
    NM_TX_PENDING = 0,
    /* The tx left the pool, e.g. because it was included in a block. */
    NM_TX_REMOVED = 1,
    /* The tx was evicted by the pool, e.g. pushed out by its capacity limit. */
    NM_TX_EVICTED = 2,
} nm_tx_event;

/*
 * Receives tx pool events; see nm_set_tx_callback.
 *
 * event:   an nm_tx_event.
 * tx_hash: 32 bytes.
 * tx:      the EIP-2718 encoding as included in blocks (without a blob sidecar).
 * tx_hash and tx are only valid for the duration of the call.
 */
typedef void (*nm_tx_callback)(void* user_data, int32_t event, const uint8_t* tx_hash, const uint8_t* tx, size_t tx_len);

typedef enum nm_node_event
{
    /* A new canonical head was processed. */
    NM_NODE_NEW_HEAD = 0,
    /* The finalized block advanced. */
    NM_NODE_FINALIZED = 1,
    /* The node caught up with the chain (eth_syncing turned false). */
    NM_NODE_SYNCED = 2,
    /* The node fell behind the chain (eth_syncing turned true). */
    NM_NODE_SYNCING = 3,
} nm_node_event;

/* The block the event is about: the new head, the finalized block, or the head at the time of a sync change. */
typedef struct nm_node_status
{
    int32_t event;
    uint64_t number;
    uint8_t hash[32];
    uint8_t state_root[32];
} nm_node_status;

/* Receives node status events; see nm_set_node_callback. status is only valid for the duration of the call. */
typedef void (*nm_node_callback)(void* user_data, const nm_node_status* status);

typedef struct nm_node nm_node;

/* A view of the state after a block; see nm_state_open. */
typedef struct nm_state nm_state;

/*
 * Starts the node in the background and returns immediately; use nm_wait_ready before other calls.
 *
 * ffi_dir:   directory containing Nethermind.FFI.dll (build or publish output of Nethermind.FFI). Relative paths in the
 *            config (db, logs, keystore) resolve against it.
 * config:    a config name from ffi_dir/configs (e.g. "mainnet") or a path to a config file.
 * overrides: "Category.Name=value" entries (e.g. "Init.BaseDbPath=/data/db"), taking precedence over NETHERMIND_*
 *            environment variables and the config file.
 * Returns NULL on failure and writes a NUL-terminated message to err.
 */
nm_node* nm_start(const char* ffi_dir, const char* config, int override_count, const char* const* overrides, char* err, size_t err_len);

/* Waits until the node has completed initialization: NM_OK, NM_NOT_READY on timeout, or NM_STOPPED. */
int nm_wait_ready(nm_node* node, int32_t timeout_ms);

/* Reads the current canonical head. */
int nm_get_head(nm_node* node, nm_head* head);

/*
 * Reads a block header as RLP into header_rlp. Always release it with nm_free_buf, whatever the status.
 *
 * block_hash: 32 bytes, or NULL for the canonical head (NM_NOT_READY while there is none).
 * Returns NM_NOT_FOUND for an unknown hash.
 */
int nm_get_header(nm_node* node, const uint8_t* block_hash, nm_buf* header_rlp);

/*
 * Writes the chain configuration as an EIP-7949 Geth-style genesis.json (config plus genesis header, without alloc) into
 * genesis_json; alloy-genesis parses it into a Genesis, from which reth builds its ChainSpec. Always release it with
 * nm_free_buf, whatever the status.
 */
int nm_get_chain_config(nm_node* node, nm_buf* genesis_json);

/*
 * Opens a view of the state after the block with the given 32-byte hash, for reads through the nm_state_* functions.
 * The view does not pin the state: once it is pruned, reads fail with NM_STATE_UNAVAILABLE. A view can be used from any
 * thread, concurrently. Close it with nm_state_close, before nm_join.
 *
 * Returns NM_NOT_FOUND for an unknown block and NM_STATE_UNAVAILABLE when its state is not available (pruned or not
 * synced).
 */
int nm_state_open(nm_node* node, const uint8_t* block_hash, nm_state** state);

void nm_state_close(nm_node* node, nm_state* state);

/* Reads the account at the 20-byte address; NM_NOT_FOUND when it does not exist. */
int nm_state_get_account(nm_node* node, nm_state* state, const uint8_t* address, nm_account* account);

/* Reads the storage slot (32 bytes, big-endian) of the 20-byte address into value, zero for a missing account or slot. */
int nm_state_get_storage(nm_node* node, nm_state* state, const uint8_t* address, const uint8_t* slot, uint8_t* value);

/*
 * Reads contract code by its 32-byte hash into code; the empty code hash yields empty code and an unknown hash
 * NM_NOT_FOUND. Always release it with nm_free_buf, whatever the status.
 */
int nm_state_get_code(nm_node* node, nm_state* state, const uint8_t* code_hash, nm_buf* code);

/*
 * Reads the 32-byte hash of the block with the given number that is the opened block or one of its ancestors, as the
 * BLOCKHASH opcode of a child block sees it. Returns NM_NOT_FOUND when number is above the opened block or more than
 * 256 blocks below it.
 */
int nm_state_get_block_hash(nm_node* node, nm_state* state, uint64_t number, uint8_t* hash);

void nm_free_buf(nm_node* node, nm_buf* buf);

/*
 * Validates and executes a block on top of its parent's state without persisting anything.
 *
 * Runs the checks of a block import: header and body validation against the parent, then execution, then comparison
 * of the computed state root, receipts root, gas used and bloom with the header. The result's roots and receipts are
 * the computed ones. Always release the result with nm_free_block_result, whatever the status.
 */
int nm_execute_block(nm_node* node, const uint8_t* block_rlp, size_t block_rlp_len, nm_block_result* result);

void nm_free_block_result(nm_node* node, nm_block_result* result);

/*
 * Sets the tx pool event callback, or clears it when callback is NULL. Requires a ready node. Only one callback can
 * be set at a time: setting another one while it is set fails with NM_CALLBACK_ALREADY_SET.
 *
 * The callback runs synchronously on whichever thread changed the pool (network, RPC or block processing), possibly
 * concurrently, so it must be thread-safe and fast: it delays the pool operation that raised it. A call already in
 * progress may still complete after the callback is cleared.
 *
 * Mirrors the pool's own events: a tx replaced by a higher-fee tx with the same sender and nonce is not reported, and
 * a tx dropped by the pool's eviction policy may be reported as NM_TX_REMOVED followed by NM_TX_EVICTED.
 */
int nm_set_tx_callback(nm_node* node, nm_tx_callback callback, void* user_data);

/*
 * Sets the node status event callback, or clears it when callback is NULL. Requires a ready node. Only one callback
 * can be set at a time: setting another one while it is set fails with NM_CALLBACK_ALREADY_SET.
 *
 * Setting the callback immediately reports the current sync state (NM_NODE_SYNCED or NM_NODE_SYNCING); after that a
 * sync event is reported only when the state changes. The sync state is the one eth_syncing reports, re-evaluated on
 * every new head and sync mode change.
 *
 * The callback runs synchronously on the thread raising the event (block processing, sync or the forkchoice update
 * caller), possibly concurrently, so it must be thread-safe and fast: it delays the operation that raised it. A call
 * already in progress may still complete after the callback is cleared.
 */
int nm_set_node_callback(nm_node* node, nm_node_callback callback, void* user_data);

/* Requests the node to shut down with the given process exit code; returns without waiting. */
int nm_stop(nm_node* node, int32_t exit_code);

/* Waits for the node to stop, releases the handle and returns the node's exit code. */
int nm_join(nm_node* node);

#ifdef __cplusplus
}
#endif

#endif

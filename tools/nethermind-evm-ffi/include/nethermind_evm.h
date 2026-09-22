/* SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
 * SPDX-License-Identifier: LGPL-3.0-only
 *
 * Nethermind's EVM behind a C ABI.
 *
 * The host owns the state. The engine borrows what it needs through the four callbacks in
 * NmEvmHost, executes one transaction per call, and returns the changes it made. It builds no
 * trie and computes no state root: the caller applies the diff to its own state and derives
 * roots itself.
 *
 * A result's changes are ordered as execution made them and must be applied in that order: an
 * account may appear more than once, and a deletion may be followed by writes to the same
 * address.
 *
 * Threading: an engine is NOT thread-safe. Create one per thread; they share nothing.
 * Strings and buffers handed to the engine are borrowed for the duration of the call only.
 * Buffers handed back live until nm_evm_result_free.
 *
 * All 256-bit integers are 32 bytes LITTLE-endian. Hashes and addresses are big-endian
 * byte strings, as they appear on chain.
 */
#ifndef NETHERMIND_EVM_H
#define NETHERMIND_EVM_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Bumped on any breaking change to the types or functions below. */
#define NM_EVM_ABI_VERSION 1u

/* ---------------------------------------------------------------- status codes */

#define NM_OK                 0
#define NM_ERR_ARGUMENT      -1
#define NM_ERR_ENGINE        -2  /* engine is null, or in a failed state */
#define NM_ERR_NO_BLOCK      -3  /* nm_evm_set_block has not been called */
#define NM_ERR_TX_REJECTED   -4  /* the transaction is invalid; not a failure of execution */
#define NM_ERR_INTERNAL      -5  /* a managed exception escaped; see nm_evm_last_error */

/* ---------------------------------------------------------------- host callbacks */

/* Every callback returns 1 when it produced a value and 0 when the item does not exist.
 * ctx is the pointer given to nm_evm_engine_new, passed back unchanged. */

/* out_balance and out_code_hash are 32 bytes each. code_hash must be keccak256("") for an
 * account with no code. */
typedef int32_t (*nm_get_account_fn)(void *ctx, const uint8_t *address, uint64_t *out_nonce,
                                     uint8_t *out_balance, uint8_t *out_code_hash);

/* key and out_value are 32 bytes each. Return 0 for an unset slot; out_value is then ignored. */
typedef int32_t (*nm_get_storage_fn)(void *ctx, const uint8_t *address, const uint8_t *key,
                                     uint8_t *out_value);

/* Returns the code length written into out_buf, or -1 when the hash is unknown. When the code is
 * longer than buf_len nothing is written and the required length is returned; the engine then
 * calls again with a large enough buffer. */
typedef int32_t (*nm_get_code_fn)(void *ctx, const uint8_t *code_hash, uint8_t *out_buf,
                                  int32_t buf_len);

/* out_hash is 32 bytes. Used by the BLOCKHASH opcode. */
typedef int32_t (*nm_get_block_hash_fn)(void *ctx, uint64_t number, uint8_t *out_hash);

typedef struct {
    void *ctx;
    nm_get_account_fn    get_account;
    nm_get_storage_fn    get_storage;
    nm_get_code_fn       get_code;
    nm_get_block_hash_fn get_block_hash;
} NmEvmHost;

/* ---------------------------------------------------------------- inputs */

typedef struct {
    uint64_t number;
    uint64_t timestamp;
    uint64_t gas_limit;
    uint8_t  coinbase[20];
    uint8_t  base_fee[32];      /* little-endian */
    uint8_t  prev_randao[32];
    uint64_t excess_blob_gas;
    int32_t  has_excess_blob_gas;
} NmEvmBlock;

/* Transaction envelope types, matching the EIP numbering on the wire. */
#define NM_TX_LEGACY   0
#define NM_TX_EIP2930  1
#define NM_TX_EIP1559  2
#define NM_TX_EIP4844  3
#define NM_TX_EIP7702  4

typedef struct {
    uint8_t  tx_type;
    uint64_t nonce;
    uint64_t gas_limit;
    uint8_t  max_fee_per_gas[32];          /* little-endian; the gas price for a legacy tx */
    uint8_t  max_priority_fee_per_gas[32]; /* little-endian */
    uint8_t  value[32];                    /* little-endian */
    uint8_t  sender[20];                   /* already recovered by the caller */
    uint8_t  to[20];
    int32_t  has_to;                       /* 0 means contract creation */
    const uint8_t *data;
    int32_t  data_len;
} NmEvmTx;

/* Execution flags. */
#define NM_EXEC_DEFAULT         0u
/* Skip nonce, balance and fee validation. For system calls and speculative execution. */
#define NM_EXEC_SKIP_VALIDATION 1u

/* ---------------------------------------------------------------- results */

/* exists == 0 means the account was deleted (self-destruct, or EIP-161 removal). */
typedef struct {
    uint8_t  address[20];
    int32_t  exists;
    uint64_t nonce;
    uint8_t  balance[32];   /* little-endian */
    uint8_t  code_hash[32];
} NmAccountChange;

typedef struct {
    uint8_t address[20];
    uint8_t key[32];        /* little-endian */
    uint8_t value[32];      /* little-endian */
} NmStorageChange;

/* Code deployed by this transaction, which the host does not yet have. */
typedef struct {
    uint8_t        code_hash[32];
    const uint8_t *code;
    int32_t        code_len;
} NmCodeChange;

typedef struct {
    uint8_t        address[20];
    int32_t        topic_count;
    const uint8_t *topics;    /* topic_count * 32 bytes, contiguous */
    const uint8_t *data;
    int32_t        data_len;
} NmLog;

typedef struct {
    int32_t  success;          /* 1 when the transaction succeeded, 0 when it reverted or failed */
    uint64_t gas_used;
    uint64_t gas_refunded;

    const uint8_t *output;
    int32_t        output_len;

    const NmAccountChange *accounts;
    int32_t                account_count;
    const NmStorageChange *storage;
    int32_t                storage_count;
    const NmCodeChange    *code;
    int32_t                code_count;
    const NmLog           *logs;
    int32_t                log_count;

    void *_arena;              /* owned by the engine; released by nm_evm_result_free */
} NmEvmResult;

/* ---------------------------------------------------------------- api */

typedef struct NmEvmEngine NmEvmEngine;

/* The ABI the library was built with. Compare against NM_EVM_ABI_VERSION before anything else. */
uint32_t nm_evm_abi_version(void);

/* Returns NULL on failure. host is copied; the function pointers and ctx must outlive the engine.
 *
 * chain_id must be 1: v1 carries mainnet's fork schedule only, and refuses rather than apply the
 * wrong rules to another chain. Fork selection then follows from the block's number and timestamp.
 */
NmEvmEngine *nm_evm_engine_new(const NmEvmHost *host, uint64_t chain_id);

void nm_evm_engine_free(NmEvmEngine *engine);

/* Sets the block every later nm_evm_execute runs in. Also fixes the fork, which is chosen from
 * the block's number and timestamp. */
int32_t nm_evm_set_block(NmEvmEngine *engine, const NmEvmBlock *block);

/* Executes one transaction. Returns NM_OK when execution ran, whatever its outcome — check
 * result->success for whether the transaction succeeded. A non-zero return means the transaction
 * never ran; result is then left untouched and must not be freed. */
int32_t nm_evm_execute(NmEvmEngine *engine, const NmEvmTx *tx, uint32_t flags, NmEvmResult *result);

/* Releases everything a result points at. Safe on a zeroed struct; not idempotent otherwise. */
void nm_evm_result_free(NmEvmResult *result);

/* Copies a build-provenance string into buf as NUL-terminated UTF-8 and returns its length
 * excluding the terminator, or the required length when buf is too small. Pass buf = NULL to ask
 * for the length. The form is:
 *
 *     nethermind-evm <version> commit=<sha>[-dirty] abi=<n>
 *
 * A host that also runs a Nethermind node should compare the commit against the node's own and
 * refuse to build on a mismatch: two EVMs from different revisions can disagree, and the
 * disagreement surfaces as an invalid block rather than an error. "-dirty" means the tree had
 * uncommitted changes and the binary corresponds to no published revision at all. */
int32_t nm_evm_build_info(uint8_t *buf, int32_t buf_len);

/* Copies the last error into buf as NUL-terminated UTF-8 and returns its length excluding the
 * terminator, or the required length when buf is too small. Pass buf = NULL to ask for the length. */
int32_t nm_evm_last_error(NmEvmEngine *engine, uint8_t *buf, int32_t buf_len);

#ifdef __cplusplus
}
#endif

#endif /* NETHERMIND_EVM_H */

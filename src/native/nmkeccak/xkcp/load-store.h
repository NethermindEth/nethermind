/*
The eXtended Keccak Code Package (XKCP)
https://github.com/XKCP/XKCP

Implementation by the XKCP contributors, hereby denoted as "the implementer".

For more information, feedback or questions, please refer to the Keccak Team website:
https://keccak.team/

To the extent possible under law, the implementer has waived all copyright
and related or neighboring rights to the source code in this file.
http://creativecommons.org/publicdomain/zero/1.0/
*/

#ifndef _load_store_h_
#define _load_store_h_

#include <stdint.h>
#include <string.h>

/*
Read and write integer lanes through byte pointers, without requiring the
pointer to be aligned and without violating strict aliasing. The memcpy()
compiles to a single load or store on architectures that support misaligned
accesses, and to a safe byte sequence on those that do not. The value uses
the host byte order, exactly like a pointer cast would.
*/

static inline uint32_t XKCP_load32(const void *p)
{
    uint32_t v;
    memcpy(&v, p, sizeof(v));
    return v;
}

static inline uint64_t XKCP_load64(const void *p)
{
    uint64_t v;
    memcpy(&v, p, sizeof(v));
    return v;
}

static inline void XKCP_store32(void *p, uint32_t v)
{
    memcpy(p, &v, sizeof(v));
}

static inline void XKCP_store64(void *p, uint64_t v)
{
    memcpy(p, &v, sizeof(v));
}

#endif

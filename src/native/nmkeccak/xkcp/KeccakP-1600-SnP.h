/*
The eXtended Keccak Code Package (XKCP)
https://github.com/XKCP/XKCP

The Keccak-p permutations, designed by Guido Bertoni, Joan Daemen, Michaël Peeters and Gilles Van Assche.

Implementation by Gilles Van Assche and Ronny Van Keer, hereby denoted as "the implementer".

For more information, feedback or questions, please refer to the Keccak Team website:
https://keccak.team/

To the extent possible under law, the implementer has waived all copyright
and related or neighboring rights to the source code in this file.
http://creativecommons.org/publicdomain/zero/1.0/

---

Please refer to SnP-documentation.h for more details.
*/

#ifndef _KeccakP_1600_SnP_h_
#define _KeccakP_1600_SnP_h_

#include "KeccakP-1600-plain64.h"

typedef KeccakP1600_plain64_state KeccakP1600_state;

#define KeccakP1600_GetImplementation               KeccakP1600_plain64_GetImplementation
#define KeccakP1600_GetFeatures                     KeccakP1600_plain64_GetFeatures

#define KeccakP1600_StaticInitialize                KeccakP1600_plain64_StaticInitialize
#define KeccakP1600_Initialize                      KeccakP1600_plain64_Initialize
#define KeccakP1600_AddByte                         KeccakP1600_plain64_AddByte
#define KeccakP1600_AddBytes                        KeccakP1600_plain64_AddBytes
#define KeccakP1600_OverwriteBytes                  KeccakP1600_plain64_OverwriteBytes
#define KeccakP1600_OverwriteWithZeroes             KeccakP1600_plain64_OverwriteWithZeroes
#define KeccakP1600_Permute_Nrounds                 KeccakP1600_plain64_Permute_Nrounds
#define KeccakP1600_Permute_12rounds                KeccakP1600_plain64_Permute_12rounds
#define KeccakP1600_Permute_24rounds                KeccakP1600_plain64_Permute_24rounds
#define KeccakP1600_ExtractBytes                    KeccakP1600_plain64_ExtractBytes
#define KeccakP1600_ExtractAndAddBytes              KeccakP1600_plain64_ExtractAndAddBytes
#define KeccakF1600_FastLoop_Absorb                 KeccakF1600_plain64_FastLoop_Absorb
#define KeccakP1600_12rounds_FastLoop_Absorb        KeccakP1600_12rounds_plain64_FastLoop_Absorb
#define KeccakP1600_ODDuplexingFastInOut            KeccakP1600_plain64_ODDuplexingFastInOut
#define KeccakP1600_12rounds_ODDuplexingFastInOut   KeccakP1600_12rounds_plain64_ODDuplexingFastInOut
#define KeccakP1600_ODDuplexingFastOut              KeccakP1600_plain64_ODDuplexingFastOut
#define KeccakP1600_12rounds_ODDuplexingFastOut     KeccakP1600_12rounds_plain64_ODDuplexingFastOut
#define KeccakP1600_ODDuplexingFastIn               KeccakP1600_plain64_ODDuplexingFastIn
#define KeccakP1600_12rounds_ODDuplexingFastIn      KeccakP1600_12rounds_plain64_ODDuplexingFastIn

#endif

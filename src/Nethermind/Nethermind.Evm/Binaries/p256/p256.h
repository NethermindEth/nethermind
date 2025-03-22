#undef my___cdecl
#undef SOEXPORT
#undef SOLOCAL
#undef DECLEXPORT

#ifdef __cplusplus
#  define my___cdecl extern "C"
#else
#  define my___cdecl
#endif

#ifndef __GNUC__
#  define __attribute__(x)
#endif

#ifdef _WIN32
#  define SOEXPORT my___cdecl __declspec(dllexport)
#  define SOLOCAL
#else
#  define DECLEXPORT my___cdecl
#  if __GNUC__ >= 4
#    define SOEXPORT my___cdecl __attribute__((visibility("default")))
#    define SOLOCAL __attribute__((visibility("hidden")))
#  else
#    define SOEXPORT my___cdecl
#    define SOLOCAL
#  endif
#endif

#ifdef _WIN32
#  undef DECLEXPORT
#  ifdef BUILDING_MYLIB
#    define DECLEXPORT __declspec(dllexport)
#  else
#    ifdef MYLIB_STATIC
#      define DECLEXPORT my___cdecl
#    else
#      define DECLEXPORT my___cdecl __declspec(dllimport)
#    endif
#  endif
#endif

#pragma once

typedef enum {
  P256_SUCCESS = 1,
  P256_INVALID_SIGNATURE = 2,
  P256_INVALID_KEYS = 3
} p256_ret_t;


#ifdef __cplusplus
extern "C" {
#endif

#include <stdint.h>
#include <stddef.h>

DECLEXPORT p256_ret_t p256_verify(uint8_t *msg, size_t msg_len, uint8_t *sig, const uint8_t *pk);

#ifdef __cplusplus
}
#endif

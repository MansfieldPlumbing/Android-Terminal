#ifndef REMEDY_HANDLE_H
#define REMEDY_HANDLE_H

#include "remedy/types.h"

#ifdef __cplusplus
extern "C" {
#endif

/* 64-bit handle: 32-bit generation, 32-bit slot index */
typedef uint64_t remedy_handle_t;

#define REMEDY_INVALID_HANDLE 0ULL

static inline uint32_t remedy_handle_slot(remedy_handle_t handle) {
    return (uint32_t)(handle & 0xFFFFFFFFULL);
}

static inline uint32_t remedy_handle_generation(remedy_handle_t handle) {
    return (uint32_t)(handle >> 32);
}

static inline remedy_handle_t remedy_handle_make(uint32_t generation, uint32_t slot) {
    return (((uint64_t)generation) << 32) | ((uint64_t)slot);
}

#ifdef __cplusplus
}
#endif

#endif // REMEDY_HANDLE_H

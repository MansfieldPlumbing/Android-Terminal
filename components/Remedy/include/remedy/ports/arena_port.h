#ifndef REMEDY_ARENA_PORT_H
#define REMEDY_ARENA_PORT_H

#include "remedy/types.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef uint64_t remedy_arena_token_t;
#define REMEDY_INVALID_ARENA_TOKEN 0ULL

/* All arena port operations return REMEDY_ERR_NOT_SUPPORTED in Milestone 1 */
static inline remedy_err_t arena_port_create(uint64_t size_bytes, remedy_arena_token_t* out_token) {
    if (out_token) *out_token = REMEDY_INVALID_ARENA_TOKEN;
    return REMEDY_ERR_NOT_SUPPORTED;
}

static inline remedy_err_t arena_port_map(remedy_arena_token_t token, uint32_t access_flags, void** out_ptr) {
    if (out_ptr) *out_ptr = NULL;
    return REMEDY_ERR_NOT_SUPPORTED;
}

static inline remedy_err_t arena_port_unmap(remedy_arena_token_t token, void* ptr) {
    return REMEDY_ERR_NOT_SUPPORTED;
}

static inline remedy_err_t arena_port_share(remedy_arena_token_t token, uint64_t worker_token, uint64_t* out_transfer_token) {
    if (out_transfer_token) *out_transfer_token = 0;
    return REMEDY_ERR_NOT_SUPPORTED;
}

static inline remedy_err_t arena_port_close(remedy_arena_token_t token) {
    return REMEDY_ERR_NOT_SUPPORTED;
}

#ifdef __cplusplus
}
#endif

#endif // REMEDY_ARENA_PORT_H

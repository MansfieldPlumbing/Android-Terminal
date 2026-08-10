#ifndef REMEDY_SLOT_PORT_H
#define REMEDY_SLOT_PORT_H

#include "remedy/types.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    uint32_t slot_id;
    uint32_t version_major;
    uint32_t version_minor;
    bool     is_active;
} remedy_slot_info_t;

/* All slot operations return REMEDY_ERR_NOT_SUPPORTED in Milestone 1 */
static inline remedy_err_t slot_port_stage(uint32_t slot_id, const char* package_path) {
    return REMEDY_ERR_NOT_SUPPORTED;
}

static inline remedy_err_t slot_port_verify(uint32_t slot_id) {
    return REMEDY_ERR_NOT_SUPPORTED;
}

static inline remedy_err_t slot_port_probe(uint32_t slot_id, remedy_slot_info_t* out_info) {
    if (out_info) {
        out_info->slot_id = slot_id;
        out_info->version_major = 0;
        out_info->version_minor = 0;
        out_info->is_active = false;
    }
    return REMEDY_ERR_NOT_SUPPORTED;
}

static inline remedy_err_t slot_port_activate(uint32_t slot_id) {
    return REMEDY_ERR_NOT_SUPPORTED;
}

static inline remedy_err_t slot_port_rollback(uint32_t slot_id) {
    return REMEDY_ERR_NOT_SUPPORTED;
}

static inline remedy_err_t slot_port_retire(uint32_t slot_id) {
    return REMEDY_ERR_NOT_SUPPORTED;
}

#ifdef __cplusplus
}
#endif

#endif // REMEDY_SLOT_PORT_H

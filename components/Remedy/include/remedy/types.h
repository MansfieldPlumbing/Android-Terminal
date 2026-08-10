#ifndef REMEDY_TYPES_H
#define REMEDY_TYPES_H

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef int32_t remedy_err_t;

#define REMEDY_OK                              0
#define REMEDY_ERR_INVALID_ARGUMENT           -1
#define REMEDY_ERR_OUT_OF_MEMORY              -2
#define REMEDY_ERR_HANDLE_STALE               -3
#define REMEDY_ERR_WRONG_TYPE                 -4
#define REMEDY_ERR_REVOKING                   -5
#define REMEDY_ERR_TIMEOUT                    -6
#define REMEDY_ERR_NOT_SUPPORTED              -7
#define REMEDY_ERR_IPC_FAILURE                -8
#define REMEDY_ERR_WAIT_FAILED                -9
#define REMEDY_ERR_WORKER_TERMINATION_FAILED  -10
#define REMEDY_ERR_CONTAINMENT_FAILED         -11
#define REMEDY_ERR_LATE_COMPLETION            -12

typedef enum {
    REMEDY_OBJECT_NONE    = 0,
    REMEDY_OBJECT_DOMAIN  = 1,
    REMEDY_OBJECT_ARENA   = 2,
    REMEDY_OBJECT_WORKER  = 3,
    REMEDY_OBJECT_CHANNEL = 4
} remedy_object_type_t;

typedef enum {
    REMEDY_SLOT_FREE     = 0,
    REMEDY_SLOT_LIVE     = 1,
    REMEDY_SLOT_REVOKING = 2,
    REMEDY_SLOT_RETIRED  = 3
} remedy_slot_state_t;

typedef enum {
    REMEDY_DOMAIN_ACTIVE      = 1,
    REMEDY_DOMAIN_REVOKING    = 2,
    REMEDY_DOMAIN_QUIESCING   = 3,
    REMEDY_DOMAIN_TERMINATING = 4,
    REMEDY_DOMAIN_DEAD        = 5,
    REMEDY_DOMAIN_FAILED      = 6
} remedy_domain_state_t;

#ifdef __cplusplus
}
#endif

#endif // REMEDY_TYPES_H

#ifndef REMEDY_ENVELOPES_H
#define REMEDY_ENVELOPES_H

#include "remedy/types.h"
#include "remedy/handle.h"

#ifdef __cplusplus
extern "C" {
#endif

#pragma pack(push, 8)

typedef struct {
    uint64_t request_id;
    remedy_handle_t domain_handle;
    remedy_handle_t lease_handle;
    uint32_t operation_id;
    remedy_handle_t payload_arena_handle;
    uint32_t payload_offset;
    uint32_t payload_length;
    uint64_t deadline_ms;
    remedy_handle_t reply_channel_handle;
} remedy_request_envelope_t;

typedef struct {
    uint64_t request_id;
    int32_t  status_code;
    remedy_handle_t result_arena_handle;
    uint32_t result_offset;
    uint32_t result_length;
    uint64_t receipt_reference;
} remedy_completion_envelope_t;

#pragma pack(pop)

#ifdef __cplusplus
}
#endif

#endif // REMEDY_ENVELOPES_H

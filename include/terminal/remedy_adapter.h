#ifndef TERMINAL_REMEDY_ADAPTER_H
#define TERMINAL_REMEDY_ADAPTER_H

#include "remedy/worker_lifecycle.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    remedy_worker_lifecycle_t lifecycle;
    uint64_t next_correlation;
} terminal_remedy_adapter_t;

remedy_err_t terminal_remedy_adapter_start(
    const char* worker_executable,
    const char* channel_name,
    terminal_remedy_adapter_t* out_adapter);

remedy_err_t terminal_remedy_adapter_send_data(
    terminal_remedy_adapter_t* adapter,
    const void* payload,
    size_t payload_length,
    uint64_t* out_correlation);

remedy_err_t terminal_remedy_adapter_read_data(
    terminal_remedy_adapter_t* adapter,
    void* payload_buffer,
    size_t payload_capacity,
    size_t* out_payload_length,
    uint64_t* out_correlation);

remedy_err_t terminal_remedy_adapter_quiesce_and_retire(
    terminal_remedy_adapter_t* adapter);

#ifdef __cplusplus
}
#endif

#endif // TERMINAL_REMEDY_ADAPTER_H

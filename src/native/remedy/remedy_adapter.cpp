#include "terminal/remedy_adapter.h"

#include <string.h>

namespace {

remedy_err_t retire_after_failure(
    terminal_remedy_adapter_t* adapter,
    remedy_err_t original_error) {
    const remedy_err_t cleanup =
        remedy_worker_lifecycle_quiesce_and_retire(&adapter->lifecycle);
    return cleanup == REMEDY_OK ? original_error : REMEDY_ERR_CONTAINMENT_FAILED;
}

}

extern "C" remedy_err_t terminal_remedy_adapter_start(
    const char* worker_executable,
    const char* channel_name,
    terminal_remedy_adapter_t* out_adapter) {
    if (!out_adapter) return REMEDY_ERR_INVALID_ARGUMENT;
    memset(out_adapter, 0, sizeof(*out_adapter));

    const remedy_worker_lifecycle_config_t config{worker_executable, channel_name};
    remedy_err_t result =
        remedy_worker_lifecycle_start(&config, &out_adapter->lifecycle);
    if (result != REMEDY_OK) return result;

    size_t completion_length = 0;
    result = remedy_worker_lifecycle_request(
        &out_adapter->lifecycle,
        1,
        nullptr,
        0,
        nullptr,
        0,
        &completion_length);
    if (result != REMEDY_OK) {
        return retire_after_failure(out_adapter, result);
    }
    if (completion_length != 0) {
        return retire_after_failure(out_adapter, REMEDY_ERR_IPC_FAILURE);
    }

    out_adapter->next_correlation = 2;
    return REMEDY_OK;
}

extern "C" remedy_err_t terminal_remedy_adapter_send_data(
    terminal_remedy_adapter_t* adapter,
    const void* payload,
    size_t payload_length,
    uint64_t* out_correlation) {
    if (!adapter || !out_correlation ||
        adapter->lifecycle.state != REMEDY_WORKER_LIFECYCLE_COMPLETED ||
        payload_length > REMEDY_MAX_PAYLOAD_SIZE ||
        (payload_length != 0 && !payload)) {
        return REMEDY_ERR_INVALID_ARGUMENT;
    }

    const uint64_t correlation = adapter->next_correlation;
    if (correlation == 0 || correlation == UINT64_MAX) {
        return REMEDY_ERR_CONTAINMENT_FAILED;
    }

    remedy_wire_frame_header_t frame{};
    frame.kind = REMEDY_WIRE_KIND_DATA;
    frame.payload_len = static_cast<uint32_t>(payload_length);
    frame.request_id = correlation;
    frame.domain_handle = adapter->lifecycle.generation;
    const remedy_err_t result =
        channel_port_send_frame(adapter->lifecycle.channel, &frame, payload);
    if (result == REMEDY_OK) {
        adapter->next_correlation = correlation + 1;
        *out_correlation = correlation;
    }
    return result;
}

extern "C" remedy_err_t terminal_remedy_adapter_read_data(
    terminal_remedy_adapter_t* adapter,
    void* payload_buffer,
    size_t payload_capacity,
    size_t* out_payload_length,
    uint64_t* out_correlation) {
    if (!adapter || !out_payload_length || !out_correlation ||
        adapter->lifecycle.state != REMEDY_WORKER_LIFECYCLE_COMPLETED ||
        (payload_capacity != 0 && !payload_buffer)) {
        return REMEDY_ERR_INVALID_ARGUMENT;
    }

    remedy_wire_frame_header_t frame{};
    const remedy_err_t result = channel_port_read_frame(
        adapter->lifecycle.channel,
        &frame,
        payload_buffer,
        payload_capacity);
    if (result != REMEDY_OK) return result;
    if (frame.kind != REMEDY_WIRE_KIND_DATA ||
        frame.domain_handle != adapter->lifecycle.generation ||
        frame.payload_len > payload_capacity) {
        return REMEDY_ERR_IPC_FAILURE;
    }

    *out_payload_length = frame.payload_len;
    *out_correlation = frame.request_id;
    return REMEDY_OK;
}

extern "C" remedy_err_t terminal_remedy_adapter_quiesce_and_retire(
    terminal_remedy_adapter_t* adapter) {
    if (!adapter) return REMEDY_ERR_INVALID_ARGUMENT;
    return remedy_worker_lifecycle_quiesce_and_retire(&adapter->lifecycle);
}

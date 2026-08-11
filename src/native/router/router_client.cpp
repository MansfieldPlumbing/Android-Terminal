#include "terminal/router_client.h"

#include <string.h>

extern "C" remedy_err_t terminal_router_client_start(
    const char* router_executable,
    const char* channel_name,
    terminal_router_client_t* out_client) {
    if (!out_client) return REMEDY_ERR_INVALID_ARGUMENT;
    memset(out_client, 0, sizeof(*out_client));
    return terminal_remedy_adapter_start(
        router_executable,
        channel_name,
        &out_client->remedy);
}

extern "C" remedy_err_t terminal_router_client_send(
    terminal_router_client_t* client,
    uint16_t operation,
    uint64_t route_id,
    uint32_t value_a,
    uint32_t value_b,
    const void* payload,
    size_t payload_length,
    uint64_t* out_correlation) {
    if (!client || !out_correlation ||
        operation < TERMINAL_ROUTER_OPEN || operation > TERMINAL_ROUTER_CLOSE ||
        payload_length > TERMINAL_ROUTER_MAX_PAYLOAD ||
        (payload_length != 0 && !payload)) {
        return REMEDY_ERR_INVALID_ARGUMENT;
    }

    uint8_t encoded[TERMINAL_ROUTER_HEADER_SIZE + TERMINAL_ROUTER_MAX_PAYLOAD]{};
    terminal_router_header_t router_header{};
    router_header.operation = operation;
    router_header.payload_len = static_cast<uint32_t>(payload_length);
    router_header.route_id = route_id;
    router_header.value_a = value_a;
    router_header.value_b = value_b;
    terminal_router_encode_header(&router_header, encoded);
    if (payload_length != 0) {
        memcpy(encoded + TERMINAL_ROUTER_HEADER_SIZE, payload, payload_length);
    }

    return terminal_remedy_adapter_send_data(
        &client->remedy,
        encoded,
        TERMINAL_ROUTER_HEADER_SIZE + payload_length,
        out_correlation);
}

extern "C" remedy_err_t terminal_router_client_read(
    terminal_router_client_t* client,
    terminal_router_message_t* out_message) {
    if (!client || !out_message) return REMEDY_ERR_INVALID_ARGUMENT;
    memset(out_message, 0, sizeof(*out_message));

    uint8_t encoded[TERMINAL_ROUTER_HEADER_SIZE + TERMINAL_ROUTER_MAX_PAYLOAD]{};
    size_t encoded_length = 0;
    uint64_t correlation = 0;
    remedy_err_t result = terminal_remedy_adapter_read_data(
        &client->remedy,
        encoded,
        sizeof(encoded),
        &encoded_length,
        &correlation);
    if (result != REMEDY_OK) return result;
    if (encoded_length < TERMINAL_ROUTER_HEADER_SIZE) {
        return REMEDY_ERR_IPC_FAILURE;
    }

    result = terminal_router_decode_header(encoded, &out_message->header);
    if (result != REMEDY_OK) return result;
    if (encoded_length !=
        TERMINAL_ROUTER_HEADER_SIZE + out_message->header.payload_len) {
        return REMEDY_ERR_IPC_FAILURE;
    }
    if (out_message->header.payload_len != 0) {
        memcpy(
            out_message->payload,
            encoded + TERMINAL_ROUTER_HEADER_SIZE,
            out_message->header.payload_len);
    }
    out_message->correlation = correlation;
    return REMEDY_OK;
}

extern "C" remedy_err_t terminal_router_client_quiesce_and_retire(
    terminal_router_client_t* client) {
    if (!client) return REMEDY_ERR_INVALID_ARGUMENT;
    return terminal_remedy_adapter_quiesce_and_retire(&client->remedy);
}

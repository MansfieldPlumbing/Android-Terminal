#ifndef TERMINAL_ROUTER_CLIENT_H
#define TERMINAL_ROUTER_CLIENT_H

#include "terminal/remedy_adapter.h"
#include "terminal/router_protocol.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    terminal_remedy_adapter_t remedy;
} terminal_router_client_t;

typedef struct {
    terminal_router_header_t header;
    uint64_t correlation;
    uint8_t payload[TERMINAL_ROUTER_MAX_PAYLOAD];
} terminal_router_message_t;

remedy_err_t terminal_router_client_start(
    const char* router_executable,
    const char* channel_name,
    terminal_router_client_t* out_client);

remedy_err_t terminal_router_client_send(
    terminal_router_client_t* client,
    uint16_t operation,
    uint64_t route_id,
    uint32_t value_a,
    uint32_t value_b,
    const void* payload,
    size_t payload_length,
    uint64_t* out_correlation);

remedy_err_t terminal_router_client_read(
    terminal_router_client_t* client,
    terminal_router_message_t* out_message);

remedy_err_t terminal_router_client_quiesce_and_retire(
    terminal_router_client_t* client);

#ifdef __cplusplus
}
#endif

#endif // TERMINAL_ROUTER_CLIENT_H

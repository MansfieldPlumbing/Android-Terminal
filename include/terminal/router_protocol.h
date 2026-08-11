#ifndef TERMINAL_ROUTER_PROTOCOL_H
#define TERMINAL_ROUTER_PROTOCOL_H

#include "remedy/types.h"

#ifdef __cplusplus
extern "C" {
#endif

#define TERMINAL_ROUTER_MAGIC          0x52545254U /* "TRTR" */
#define TERMINAL_ROUTER_VERSION        1U
#define TERMINAL_ROUTER_HEADER_SIZE    32U
#define TERMINAL_ROUTER_MAX_PAYLOAD    8192U

typedef enum {
    TERMINAL_ROUTER_OPEN    = 1,
    TERMINAL_ROUTER_OPENED  = 2,
    TERMINAL_ROUTER_INPUT   = 3,
    TERMINAL_ROUTER_OUTPUT  = 4,
    TERMINAL_ROUTER_RESIZE  = 5,
    TERMINAL_ROUTER_RESIZED = 6,
    TERMINAL_ROUTER_CLOSE   = 7,
    TERMINAL_ROUTER_CLOSED  = 8,
    TERMINAL_ROUTER_ERROR   = 9
} terminal_router_operation_t;

typedef struct {
    uint16_t operation;
    uint32_t payload_len;
    uint64_t route_id;
    uint32_t value_a;
    uint32_t value_b;
} terminal_router_header_t;

static inline void terminal_router_encode_header(
    const terminal_router_header_t* header,
    uint8_t out[TERMINAL_ROUTER_HEADER_SIZE]) {
    const uint32_t magic = TERMINAL_ROUTER_MAGIC;
    out[0] = (uint8_t)magic;
    out[1] = (uint8_t)(magic >> 8);
    out[2] = (uint8_t)(magic >> 16);
    out[3] = (uint8_t)(magic >> 24);
    out[4] = (uint8_t)TERMINAL_ROUTER_VERSION;
    out[5] = 0;
    out[6] = (uint8_t)header->operation;
    out[7] = (uint8_t)(header->operation >> 8);
    out[8] = (uint8_t)TERMINAL_ROUTER_HEADER_SIZE;
    out[9] = 0;
    out[10] = 0;
    out[11] = 0;
    out[12] = (uint8_t)header->payload_len;
    out[13] = (uint8_t)(header->payload_len >> 8);
    out[14] = (uint8_t)(header->payload_len >> 16);
    out[15] = (uint8_t)(header->payload_len >> 24);
    for (uint32_t i = 0; i < 8; ++i) out[16 + i] = (uint8_t)(header->route_id >> (i * 8));
    out[24] = (uint8_t)header->value_a;
    out[25] = (uint8_t)(header->value_a >> 8);
    out[26] = (uint8_t)(header->value_a >> 16);
    out[27] = (uint8_t)(header->value_a >> 24);
    out[28] = (uint8_t)header->value_b;
    out[29] = (uint8_t)(header->value_b >> 8);
    out[30] = (uint8_t)(header->value_b >> 16);
    out[31] = (uint8_t)(header->value_b >> 24);
}

static inline remedy_err_t terminal_router_decode_header(
    const uint8_t in[TERMINAL_ROUTER_HEADER_SIZE],
    terminal_router_header_t* out_header) {
    if (!in || !out_header) return REMEDY_ERR_INVALID_ARGUMENT;
    const uint32_t magic =
        (uint32_t)in[0] | ((uint32_t)in[1] << 8) |
        ((uint32_t)in[2] << 16) | ((uint32_t)in[3] << 24);
    const uint16_t version = (uint16_t)(in[4] | ((uint16_t)in[5] << 8));
    const uint16_t header_len = (uint16_t)(in[8] | ((uint16_t)in[9] << 8));
    if (magic != TERMINAL_ROUTER_MAGIC ||
        version != TERMINAL_ROUTER_VERSION ||
        header_len != TERMINAL_ROUTER_HEADER_SIZE ||
        in[10] != 0 || in[11] != 0) return REMEDY_ERR_IPC_FAILURE;

    out_header->operation = (uint16_t)(in[6] | ((uint16_t)in[7] << 8));
    out_header->payload_len =
        (uint32_t)in[12] | ((uint32_t)in[13] << 8) |
        ((uint32_t)in[14] << 16) | ((uint32_t)in[15] << 24);
    out_header->route_id = 0;
    for (uint32_t i = 0; i < 8; ++i) out_header->route_id |= ((uint64_t)in[16 + i]) << (i * 8);
    out_header->value_a =
        (uint32_t)in[24] | ((uint32_t)in[25] << 8) |
        ((uint32_t)in[26] << 16) | ((uint32_t)in[27] << 24);
    out_header->value_b =
        (uint32_t)in[28] | ((uint32_t)in[29] << 8) |
        ((uint32_t)in[30] << 16) | ((uint32_t)in[31] << 24);
    if (out_header->operation < TERMINAL_ROUTER_OPEN ||
        out_header->operation > TERMINAL_ROUTER_ERROR ||
        out_header->payload_len > TERMINAL_ROUTER_MAX_PAYLOAD) return REMEDY_ERR_IPC_FAILURE;
    return REMEDY_OK;
}

#ifdef __cplusplus
}
#endif

#endif // TERMINAL_ROUTER_PROTOCOL_H

#ifndef REMEDY_WIRE_FRAME_H
#define REMEDY_WIRE_FRAME_H

#include "remedy/types.h"
#include <string.h>

#ifdef __cplusplus
extern "C" {
#endif

#define REMEDY_WIRE_MAGIC         0x52454D44U // "REMD"
#define REMEDY_WIRE_VERSION       1U
#define REMEDY_WIRE_HEADER_SIZE   36U
#define REMEDY_MAX_PAYLOAD_SIZE   (1024U * 1024U) // 1 MB max payload safety limit

typedef enum {
    REMEDY_WIRE_KIND_REQUEST     = 1,
    REMEDY_WIRE_KIND_COMPLETION  = 2,
    REMEDY_WIRE_KIND_PING        = 3,
    REMEDY_WIRE_KIND_PONG        = 4,
    REMEDY_WIRE_KIND_QUIESCE     = 5,
    REMEDY_WIRE_KIND_QUIESCE_ACK = 6
} remedy_wire_kind_t;

typedef struct {
    uint32_t magic;         // REMEDY_WIRE_MAGIC
    uint16_t version;       // REMEDY_WIRE_VERSION
    uint16_t kind;          // remedy_wire_kind_t
    uint16_t header_len;    // REMEDY_WIRE_HEADER_SIZE (36)
    uint16_t reserved;      // 0
    uint32_t payload_len;   // length of payload buffer
    uint64_t request_id;    // monotonic request identifier
    uint64_t domain_handle; // owning domain 64-bit handle
    uint32_t checksum;      // Adler32 payload checksum
} remedy_wire_frame_header_t;

static inline uint32_t remedy_adler32(const uint8_t* data, size_t len) {
    uint32_t a = 1, b = 0;
    for (size_t i = 0; i < len; ++i) {
        a = (a + data[i]) % 65521;
        b = (b + a) % 65521;
    }
    return (b << 16) | a;
}

/* Explicit Little-Endian Serialization */
static inline void remedy_wire_frame_encode(const remedy_wire_frame_header_t* hdr, uint8_t out[36]) {
    // magic (0..3)
    out[0] = (uint8_t)(hdr->magic & 0xFF);
    out[1] = (uint8_t)((hdr->magic >> 8) & 0xFF);
    out[2] = (uint8_t)((hdr->magic >> 16) & 0xFF);
    out[3] = (uint8_t)((hdr->magic >> 24) & 0xFF);

    // version (4..5)
    out[4] = (uint8_t)(hdr->version & 0xFF);
    out[5] = (uint8_t)((hdr->version >> 8) & 0xFF);

    // kind (6..7)
    out[6] = (uint8_t)(hdr->kind & 0xFF);
    out[7] = (uint8_t)((hdr->kind >> 8) & 0xFF);

    // header_len (8..9)
    out[8] = (uint8_t)(hdr->header_len & 0xFF);
    out[9] = (uint8_t)((hdr->header_len >> 8) & 0xFF);

    // reserved (10..11)
    out[10] = (uint8_t)(hdr->reserved & 0xFF);
    out[11] = (uint8_t)((hdr->reserved >> 8) & 0xFF);

    // payload_len (12..15)
    out[12] = (uint8_t)(hdr->payload_len & 0xFF);
    out[13] = (uint8_t)((hdr->payload_len >> 8) & 0xFF);
    out[14] = (uint8_t)((hdr->payload_len >> 16) & 0xFF);
    out[15] = (uint8_t)((hdr->payload_len >> 24) & 0xFF);

    // request_id (16..23)
    out[16] = (uint8_t)(hdr->request_id & 0xFF);
    out[17] = (uint8_t)((hdr->request_id >> 8) & 0xFF);
    out[18] = (uint8_t)((hdr->request_id >> 16) & 0xFF);
    out[19] = (uint8_t)((hdr->request_id >> 24) & 0xFF);
    out[20] = (uint8_t)((hdr->request_id >> 32) & 0xFF);
    out[21] = (uint8_t)((hdr->request_id >> 40) & 0xFF);
    out[22] = (uint8_t)((hdr->request_id >> 48) & 0xFF);
    out[23] = (uint8_t)((hdr->request_id >> 56) & 0xFF);

    // domain_handle (24..31)
    out[24] = (uint8_t)(hdr->domain_handle & 0xFF);
    out[25] = (uint8_t)((hdr->domain_handle >> 8) & 0xFF);
    out[26] = (uint8_t)((hdr->domain_handle >> 16) & 0xFF);
    out[27] = (uint8_t)((hdr->domain_handle >> 24) & 0xFF);
    out[28] = (uint8_t)((hdr->domain_handle >> 32) & 0xFF);
    out[29] = (uint8_t)((hdr->domain_handle >> 40) & 0xFF);
    out[30] = (uint8_t)((hdr->domain_handle >> 48) & 0xFF);
    out[31] = (uint8_t)((hdr->domain_handle >> 56) & 0xFF);

    // checksum (32..35)
    out[32] = (uint8_t)(hdr->checksum & 0xFF);
    out[33] = (uint8_t)((hdr->checksum >> 8) & 0xFF);
    out[34] = (uint8_t)((hdr->checksum >> 16) & 0xFF);
    out[35] = (uint8_t)((hdr->checksum >> 24) & 0xFF);
}

/* Explicit Little-Endian Deserialization */
static inline remedy_err_t remedy_wire_frame_decode(const uint8_t in[36], remedy_wire_frame_header_t* hdr) {
    if (!in || !hdr) return REMEDY_ERR_INVALID_ARGUMENT;

    hdr->magic = ((uint32_t)in[0]) |
                 (((uint32_t)in[1]) << 8) |
                 (((uint32_t)in[2]) << 16) |
                 (((uint32_t)in[3]) << 24);

    hdr->version = ((uint16_t)in[4]) |
                   (((uint16_t)in[5]) << 8);

    hdr->kind = ((uint16_t)in[6]) |
                (((uint16_t)in[7]) << 8);

    hdr->header_len = ((uint16_t)in[8]) |
                      (((uint16_t)in[9]) << 8);

    hdr->reserved = ((uint16_t)in[10]) |
                    (((uint16_t)in[11]) << 8);

    hdr->payload_len = ((uint32_t)in[12]) |
                       (((uint32_t)in[13]) << 8) |
                       (((uint32_t)in[14]) << 16) |
                       (((uint32_t)in[15]) << 24);

    hdr->request_id = ((uint64_t)in[16]) |
                      (((uint64_t)in[17]) << 8) |
                      (((uint64_t)in[18]) << 16) |
                      (((uint64_t)in[19]) << 24) |
                      (((uint64_t)in[20]) << 32) |
                      (((uint64_t)in[21]) << 40) |
                      (((uint64_t)in[22]) << 48) |
                      (((uint64_t)in[23]) << 56);

    hdr->domain_handle = ((uint64_t)in[24]) |
                         (((uint64_t)in[25]) << 8) |
                         (((uint64_t)in[26]) << 16) |
                         (((uint64_t)in[27]) << 24) |
                         (((uint64_t)in[28]) << 32) |
                         (((uint64_t)in[29]) << 40) |
                         (((uint64_t)in[30]) << 48) |
                         (((uint64_t)in[31]) << 56);

    hdr->checksum = ((uint32_t)in[32]) |
                    (((uint32_t)in[33]) << 8) |
                    (((uint32_t)in[34]) << 16) |
                    (((uint32_t)in[35]) << 24);

    // Hardened Header Validation
    if (hdr->magic != REMEDY_WIRE_MAGIC) return REMEDY_ERR_IPC_FAILURE;
    if (hdr->version != REMEDY_WIRE_VERSION) return REMEDY_ERR_IPC_FAILURE;
    if (hdr->header_len != REMEDY_WIRE_HEADER_SIZE) return REMEDY_ERR_IPC_FAILURE;
    if (hdr->payload_len > REMEDY_MAX_PAYLOAD_SIZE) return REMEDY_ERR_INVALID_ARGUMENT;
    if (hdr->kind < REMEDY_WIRE_KIND_REQUEST || hdr->kind > REMEDY_WIRE_KIND_QUIESCE_ACK) return REMEDY_ERR_IPC_FAILURE;

    return REMEDY_OK;
}

#ifdef __cplusplus
}
#endif

#endif // REMEDY_WIRE_FRAME_H

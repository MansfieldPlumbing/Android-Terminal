#include "remedy/wire_frame.h"
#include <iostream>
#include <cassert>

static const uint8_t GOLDEN_PING_FRAME[36] = {
    0x44, 0x4D, 0x45, 0x52, // magic: 0x52454D44 ("REMD" LE: 0x44, 0x4D, 0x45, 0x52)
    0x01, 0x00,             // version: 1 (0x0001 LE)
    0x03, 0x00,             // kind: 3 (PING LE)
    0x24, 0x00,             // header_len: 36 (0x0024 LE)
    0x00, 0x00,             // reserved: 0
    0x00, 0x00, 0x00, 0x00, // payload_len: 0
    0xE9, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // request_id: 1001
    0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, // domain_handle: 0x100000002
    0x00, 0x00, 0x00, 0x00  // checksum: 0
};

int main() {
    std::cout << "[TEST] Starting Wire Frame Little-Endian Golden Codec Test..." << std::endl;

    // 1. Encode Verification against Golden Vector
    remedy_wire_frame_header_t hdr1 = { 0 };
    hdr1.magic = REMEDY_WIRE_MAGIC;
    hdr1.version = REMEDY_WIRE_VERSION;
    hdr1.kind = REMEDY_WIRE_KIND_PING;
    hdr1.header_len = REMEDY_WIRE_HEADER_SIZE;
    hdr1.payload_len = 0;
    hdr1.request_id = 1001;
    hdr1.domain_handle = 0x100000002ULL;
    hdr1.checksum = 0;

    uint8_t encoded[36] = { 0 };
    remedy_wire_frame_encode(&hdr1, encoded);

    for (int i = 0; i < 36; ++i) {
        assert(encoded[i] == GOLDEN_PING_FRAME[i]);
    }
    std::cout << "[TEST] Encode matches Golden Vector 100%!" << std::endl;

    // 2. Decode Verification from Golden Vector
    remedy_wire_frame_header_t hdr2 = { 0 };
    remedy_err_t dec_res = remedy_wire_frame_decode(GOLDEN_PING_FRAME, &hdr2);
    assert(dec_res == REMEDY_OK);
    assert(hdr2.magic == REMEDY_WIRE_MAGIC);
    assert(hdr2.version == REMEDY_WIRE_VERSION);
    assert(hdr2.kind == REMEDY_WIRE_KIND_PING);
    assert(hdr2.header_len == 36);
    assert(hdr2.payload_len == 0);
    assert(hdr2.request_id == 1001);
    assert(hdr2.domain_handle == 0x100000002ULL);
    assert(hdr2.checksum == 0);
    std::cout << "[TEST] Decode from Golden Vector 100% successful!" << std::endl;

    // 3. Malformed Frame Rejection Tests
    uint8_t bad_magic[36];
    memcpy(bad_magic, GOLDEN_PING_FRAME, 36);
    bad_magic[0] = 0xFF; // Corrupt magic
    assert(remedy_wire_frame_decode(bad_magic, &hdr2) == REMEDY_ERR_IPC_FAILURE);

    uint8_t bad_version[36];
    memcpy(bad_version, GOLDEN_PING_FRAME, 36);
    bad_version[4] = 0x02; // Corrupt version 2
    assert(remedy_wire_frame_decode(bad_version, &hdr2) == REMEDY_ERR_IPC_FAILURE);

    uint8_t bad_hdr_len[36];
    memcpy(bad_hdr_len, GOLDEN_PING_FRAME, 36);
    bad_hdr_len[8] = 0x20; // Header len 32 instead of 36
    assert(remedy_wire_frame_decode(bad_hdr_len, &hdr2) == REMEDY_ERR_IPC_FAILURE);

    uint8_t bad_kind[36];
    memcpy(bad_kind, GOLDEN_PING_FRAME, 36);
    bad_kind[6] = 0xFF; // Unknown kind
    assert(remedy_wire_frame_decode(bad_kind, &hdr2) == REMEDY_ERR_IPC_FAILURE);

    uint8_t overflow_payload[36];
    memcpy(overflow_payload, GOLDEN_PING_FRAME, 36);
    overflow_payload[12] = 0x00;
    overflow_payload[13] = 0x00;
    overflow_payload[14] = 0x20; // 2,097,152 bytes (> 1 MB)
    overflow_payload[15] = 0x00;
    assert(remedy_wire_frame_decode(overflow_payload, &hdr2) == REMEDY_ERR_INVALID_ARGUMENT);

    std::cout << "[TEST] All Malformed Frame Rejection Tests PASSED!" << std::endl;
    return 0;
}

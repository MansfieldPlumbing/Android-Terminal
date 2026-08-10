#ifndef REMEDY_CHANNEL_PORT_H
#define REMEDY_CHANNEL_PORT_H

#include "remedy/types.h"
#include "remedy/wire_frame.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef uint64_t remedy_channel_token_t;

#define REMEDY_INVALID_CHANNEL_TOKEN 0ULL

typedef struct {
    const char* channel_name;
    bool        is_server;
} remedy_channel_config_t;

remedy_err_t channel_port_create(const remedy_channel_config_t* config, remedy_channel_token_t* out_token);
remedy_err_t channel_port_connect(remedy_channel_token_t token, uint32_t timeout_ms);
remedy_err_t channel_port_send_frame(remedy_channel_token_t token, const remedy_wire_frame_header_t* header, const void* payload);
remedy_err_t channel_port_read_frame(remedy_channel_token_t token, remedy_wire_frame_header_t* out_header, void* payload_buffer, size_t max_payload_len);
remedy_err_t channel_port_close(remedy_channel_token_t token);
remedy_err_t channel_port_destroy(remedy_channel_token_t token);

#ifdef __cplusplus
}
#endif

#endif // REMEDY_CHANNEL_PORT_H

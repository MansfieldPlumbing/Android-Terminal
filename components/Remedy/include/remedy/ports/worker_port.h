#ifndef REMEDY_WORKER_PORT_H
#define REMEDY_WORKER_PORT_H

#include "remedy/types.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef uint64_t remedy_worker_token_t;

#define REMEDY_INVALID_WORKER_TOKEN 0ULL

typedef struct {
    const char* executable_path;
    const char* arguments;
    const char* working_directory;
    const char* channel_nonce;
    uint32_t    timeout_ms;
} remedy_worker_config_t;

remedy_err_t worker_port_start(const remedy_worker_config_t* config, remedy_worker_token_t* out_token);
remedy_err_t worker_port_request_quiescence(remedy_worker_token_t token);
remedy_err_t worker_port_terminate(remedy_worker_token_t token);
remedy_err_t worker_port_wait_for_death(remedy_worker_token_t token, uint32_t timeout_ms, bool* out_died);
remedy_err_t worker_port_destroy(remedy_worker_token_t token);

#ifdef __cplusplus
}
#endif

#endif // REMEDY_WORKER_PORT_H

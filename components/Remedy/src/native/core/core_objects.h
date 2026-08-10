#ifndef REMEDY_CORE_OBJECTS_H
#define REMEDY_CORE_OBJECTS_H

#include "remedy/types.h"
#include "remedy/handle.h"
#include "remedy/ports/worker_port.h"
#include "remedy/ports/channel_port.h"

namespace remedy {

class domain;

class worker_object {
public:
    explicit worker_object(remedy_worker_token_t token) : token_(token) {}
    ~worker_object() {
        if (token_ != REMEDY_INVALID_WORKER_TOKEN) {
            worker_port_destroy(token_);
            token_ = REMEDY_INVALID_WORKER_TOKEN;
        }
    }

    remedy_worker_token_t token() const { return token_; }

private:
    remedy_worker_token_t token_{REMEDY_INVALID_WORKER_TOKEN};
};

class channel_object {
public:
    explicit channel_object(remedy_channel_token_t token) : token_(token) {}
    ~channel_object() {
        if (token_ != REMEDY_INVALID_CHANNEL_TOKEN) {
            channel_port_close(token_);
            channel_port_destroy(token_);
            token_ = REMEDY_INVALID_CHANNEL_TOKEN;
        }
    }

    remedy_channel_token_t token() const { return token_; }

private:
    remedy_channel_token_t token_{REMEDY_INVALID_CHANNEL_TOKEN};
};

class domain_object {
public:
    explicit domain_object(domain* d) : domain_ptr_(d) {}
    ~domain_object(); // Implemented after domain declaration

    domain* get() const { return domain_ptr_; }

private:
    domain* domain_ptr_{nullptr};
};

} // namespace remedy

#endif // REMEDY_CORE_OBJECTS_H

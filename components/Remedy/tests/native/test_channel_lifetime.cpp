#define NOMINMAX
#include <windows.h>
#ifdef COMPLETED
#undef COMPLETED
#endif
#ifdef CANCELED
#undef CANCELED
#endif
#ifdef TIMED_OUT
#undef TIMED_OUT
#endif

#include <cstdint>
#include <cstddef>
#include <cstdio>
#include <mutex>
#include <condition_variable>
#include <thread>
#include <chrono>
#include <string>

#include "remedy/ports/channel_port.h"
#include "remedy/wire_frame.h"

#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
struct remedy_test_channel_operation_history {
    bool found{false}; bool created{false}; bool io_submitted{false}; bool io_was_pending{false};
    bool public_call_completed{false}; bool kernel_completion_observed{false};
    bool detached_at_public_return{false}; bool detached_recovered{false};
    bool no_kernel_request_outstanding_at_public_return{true};
    remedy_err_t final_public_result{REMEDY_OK}; DWORD final_completion_error{ERROR_SUCCESS};
};

struct channel_finalizer_owner_observation {
    remedy_channel_token_t token{REMEDY_INVALID_CHANNEL_TOKEN};
    uint64_t generation_number{0};
    uint64_t owner_call_cookie{0};
    bool finalizer_active{false};
    bool draining{false};
    bool public_destroy_completed{false};
    remedy_err_t finalizer_result{REMEDY_OK};
    bool entry_remains_registered{true};
};

extern "C" {
    [[noreturn]] void remedy_test_catastrophic_harness_failure(
        const char* message);
    bool remedy_test_channel_reset_seam(void);
    bool remedy_test_channel_get_operation_history(remedy_channel_token_t token, uint32_t operation_kind, uint64_t instance_id, remedy_test_channel_operation_history* out_history);
    bool remedy_test_channel_set_operation_call_cookie(uint64_t cookie);
    bool remedy_test_channel_set_destroy_call_cookie(uint64_t cookie);
    bool remedy_test_channel_take_last_created_operation_instance(uint64_t* out_instance_id);
    bool remedy_test_channel_wait_operation_created(remedy_channel_token_t token, uint32_t operation_kind, uint64_t call_cookie, uint32_t timeout_ms, uint64_t* out_instance_id);
    bool remedy_test_channel_wait_operation_pending(remedy_channel_token_t token, uint32_t operation_kind, uint64_t instance_id, uint32_t timeout_ms);
    bool remedy_test_channel_arm_lease_pause(remedy_channel_token_t token);
    bool remedy_test_channel_wait_lease_paused(remedy_channel_token_t token, uint32_t timeout_ms);
    bool remedy_test_channel_release_lease_pause(void);
    bool remedy_test_channel_arm_submission_pause(remedy_channel_token_t token, uint32_t operation_kind, uint64_t call_cookie);
    bool remedy_test_channel_wait_submission_paused(remedy_channel_token_t token, uint32_t operation_kind, uint64_t call_cookie, uint32_t timeout_ms, uint64_t* out_instance_id);
    bool remedy_test_channel_release_submission_pause(void);
    bool remedy_test_channel_arm_finalizer_pause(remedy_channel_token_t token);
    bool remedy_test_channel_wait_finalizer_paused(remedy_channel_token_t token, uint32_t timeout_ms);
    bool remedy_test_channel_release_finalizer_pause(void);
    bool remedy_test_channel_wait_finalizer_draining(remedy_channel_token_t token, uint32_t timeout_ms);
    bool remedy_test_channel_arm_close_pause(remedy_channel_token_t token);
    bool remedy_test_channel_wait_close_paused(remedy_channel_token_t token, uint32_t timeout_ms);
    bool remedy_test_channel_release_close_pause(void);
    bool remedy_test_channel_inject_pipe_creation_failure(DWORD sim_error);
    bool remedy_test_channel_inject_pipe_close_failure(remedy_channel_token_t token);
    bool remedy_test_channel_inject_event_close_failure(remedy_channel_token_t token, uint32_t operation_kind);
    bool remedy_test_channel_inject_pipe_cancel_failure(remedy_channel_token_t token, DWORD simulated_error);
    bool remedy_test_channel_inject_operation_cancel_failure(remedy_channel_token_t token, uint32_t operation_kind, DWORD simulated_error);
    bool remedy_test_channel_inject_wait_failure(remedy_channel_token_t token, uint32_t operation_kind, DWORD simulated_error);
    bool remedy_test_channel_get_finalizer_owner_observation(remedy_channel_token_t token, channel_finalizer_owner_observation* out_obs);
    bool remedy_test_channel_publish_competitor_attached(remedy_channel_token_t token, uint64_t generation_number, uint64_t competitor_cookie);
    bool remedy_test_channel_wait_competitor_attached(remedy_channel_token_t token, uint64_t generation_number, uint32_t timeout_ms, uint64_t* out_competitor_cookie);
    bool remedy_test_channel_get_entry_snapshot(remedy_channel_token_t token, int* out_state, uint32_t* out_leases, uint32_t* out_close_pins, bool* out_finalizer_active, bool* out_in_registry, bool* out_is_pipe_null, uint32_t* out_retained_event_count);
    bool remedy_test_channel_get_operation_snapshot(remedy_channel_token_t token, uint32_t operation_kind, uint64_t instance_id, int* out_state, bool* out_io_submitted, bool* out_io_pending, bool* out_completion_observed, bool* out_detached, bool* out_event_owned, size_t* out_owned_buffer_size);
    bool remedy_test_channel_get_final_counts(uint32_t* out_active_registry_entries, uint32_t* out_active_public_leases, uint32_t* out_active_close_pins, uint32_t* out_retained_operation_records, uint32_t* out_submitted_pending_operations, uint32_t* out_detached_pending_operations, uint32_t* out_completed_retained_operations, uint32_t* out_operation_buffers_allocated, uint32_t* out_operation_buffers_freed, uint32_t* out_detached_operations_created, uint32_t* out_detached_operations_recovered, uint32_t* out_unconsumed_injections, uint32_t* out_armed_pause_count, uint32_t* out_pending_operation_count, uint32_t* out_finalizer_invocations, uint32_t* out_finalizer_completions, uint32_t* out_successful_finalizers, uint32_t* out_entry_deletions, uint32_t* out_pipe_closures, uint32_t* out_event_closures, uint32_t* out_pipe_close_failures_consumed, uint32_t* out_event_close_failures_consumed, uint32_t* out_pipe_cancel_failures_consumed, uint32_t* out_operation_cancel_failures_consumed, uint32_t* out_wait_failures_consumed);
    bool remedy_test_channel_set_next_token(remedy_channel_token_t next_token, remedy_channel_token_t* out_previous);
}
#endif

enum class test_thread_kind { CONNECT, READER, WRITER, CLOSE, DESTROY_A, DESTROY_B };

static inline remedy_wire_frame_header_t make_test_header(uint32_t payload_len, uint32_t payload_checksum) {
    remedy_wire_frame_header_t hdr{};
    hdr.magic = REMEDY_WIRE_MAGIC;
    hdr.version = REMEDY_WIRE_VERSION;
    hdr.kind = REMEDY_WIRE_KIND_REQUEST;
    hdr.header_len = REMEDY_WIRE_HEADER_SIZE;
    hdr.reserved = 0;
    hdr.payload_len = payload_len;
    hdr.request_id = 100;
    hdr.domain_handle = 200;
    hdr.checksum = payload_checksum;
    return hdr;
}

struct token_override_guard {
    remedy_channel_token_t previous_token{REMEDY_INVALID_CHANNEL_TOKEN};
    remedy_channel_token_t target_token{REMEDY_INVALID_CHANNEL_TOKEN};
    struct test_channel_context_t* ctx{nullptr};

    token_override_guard(remedy_channel_token_t override_val, struct test_channel_context_t& context)
        : target_token(override_val), ctx(&context) {
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
        remedy_test_channel_set_next_token(target_token, &previous_token);
#endif
    }
    ~token_override_guard() {
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
        remedy_channel_token_t reported_prev = REMEDY_INVALID_CHANNEL_TOKEN;
        remedy_test_channel_set_next_token(previous_token, &reported_prev);
#endif
    }
};

struct test_channel_context_t {
    std::mutex thread_state_mutex; std::condition_variable thread_state_cv;
    remedy_channel_token_t server_token{REMEDY_INVALID_CHANNEL_TOKEN};
    remedy_channel_token_t client_token{REMEDY_INVALID_CHANNEL_TOKEN};
    bool server_created{false}; bool client_created{false};
    bool server_close_succeeded{false}; bool client_close_succeeded{false};
    bool server_destroy_succeeded{false}; bool client_destroy_succeeded{false};

    remedy_channel_token_t connect_token{REMEDY_INVALID_CHANNEL_TOKEN};
    remedy_channel_token_t connect_peer_token{REMEDY_INVALID_CHANNEL_TOKEN};
    remedy_channel_token_t reader_token{REMEDY_INVALID_CHANNEL_TOKEN};
    remedy_channel_token_t reader_peer_token{REMEDY_INVALID_CHANNEL_TOKEN};
    remedy_channel_token_t writer_token{REMEDY_INVALID_CHANNEL_TOKEN};
    remedy_channel_token_t writer_peer_token{REMEDY_INVALID_CHANNEL_TOKEN};

    uint64_t destroy_a_cookie{101}; uint64_t destroy_b_cookie{102};

    bool connect_thread_started{false}; bool reader_thread_started{false}; bool writer_thread_started{false}; bool close_thread_started{false};
    bool destroy_thread_a_started{false}; bool destroy_thread_b_started{false};

    bool connect_thread_joined{false}; bool reader_thread_joined{false}; bool writer_thread_joined{false}; bool close_thread_joined{false};
    bool destroy_thread_a_joined{false}; bool destroy_thread_b_joined{false};

    bool connect_public_call_completed{false}; bool reader_public_call_completed{false}; bool writer_public_call_completed{false}; bool close_public_call_completed{false};
    bool destroy_a_public_call_completed{false}; bool destroy_b_public_call_completed{false};

    uint64_t connect_instance_id{0}; uint64_t reader_instance_id{0}; uint64_t writer_instance_id{0};
    remedy_err_t connect_operation_result{REMEDY_OK}; remedy_err_t reader_operation_result{REMEDY_OK}; remedy_err_t writer_operation_result{REMEDY_OK}; remedy_err_t close_operation_result{REMEDY_OK};
    remedy_err_t destroy_a_result{REMEDY_OK}; remedy_err_t destroy_b_result{REMEDY_OK};

    std::thread connect_thread; std::thread reader_thread; std::thread writer_thread; std::thread close_thread;
    std::thread destroy_thread_a; std::thread destroy_thread_b;

    std::string primary_error; std::string cleanup_errors;

    void record_primary(const std::string& msg) {
        std::lock_guard<std::mutex> lock(thread_state_mutex);
        if (primary_error.empty()) primary_error = msg; else primary_error += "; " + msg;
    }
    void record_cleanup(const std::string& msg) {
        std::lock_guard<std::mutex> lock(thread_state_mutex);
        if (cleanup_errors.empty()) cleanup_errors = msg; else cleanup_errors += "; " + msg;
    }

    bool create_and_connect_test_channel_pair(const char* channel_name, uint64_t connect_cookie) {
        remedy_channel_config_t server_cfg{channel_name, true};
        remedy_channel_config_t client_cfg{channel_name, false};

        if (channel_port_create(&server_cfg, &server_token) != REMEDY_OK) {
            record_primary("Server creation failed"); return false;
        }
        server_created = true;

        if (channel_port_create(&client_cfg, &client_token) != REMEDY_OK) {
            record_primary("Client creation failed; performing checked server cleanup");
            channel_port_close(server_token);
            if (!destroy_endpoint_with_documented_retries(server_token, destroy_a_cookie)) {
                remedy_test_catastrophic_harness_failure(
                    "Server cleanup failed after client creation failure");
            }
            server_token = REMEDY_INVALID_CHANNEL_TOKEN; server_created = false;
            return false;
        }
        client_created = true;

        connect_token = server_token; connect_peer_token = client_token;
        connect_thread = std::thread(&test_channel_context_t::run_connect_thread_wrapper, this, server_token, 5000, connect_cookie);

        if (!wait_for_thread_completion(test_thread_kind::CONNECT, 2000)) {
            record_primary("Connect thread failed to complete");

            if (server_token != REMEDY_INVALID_CHANNEL_TOKEN) {
                channel_port_close(server_token);
            }
            if (client_token != REMEDY_INVALID_CHANNEL_TOKEN) {
                channel_port_close(client_token);
            }

            if (!wait_for_thread_completion(
                    test_thread_kind::CONNECT,
                    6000)) {
                remedy_test_catastrophic_harness_failure(
                    "Connect thread did not complete after bounded cleanup unblock");
            }

            if (connect_thread.joinable()) {
                connect_thread.join();
            }
            connect_thread_joined = true;
            return false;
        }

        if (connect_thread.joinable()) connect_thread.join();
        connect_thread_joined = true;

        if (connect_operation_result != REMEDY_OK) {
            record_primary("Connect operation returned non-OK result"); return false;
        }
        return true;
    }

    void run_connect_thread_wrapper(remedy_channel_token_t tok, uint32_t timeout_ms, uint64_t cookie) {
        { std::lock_guard<std::mutex> lock(thread_state_mutex); connect_thread_started = true; }
        remedy_test_channel_set_operation_call_cookie(cookie);
        remedy_err_t res = channel_port_connect(tok, timeout_ms);
        uint64_t inst_id = 0;
        bool instance_ok = remedy_test_channel_take_last_created_operation_instance(&inst_id);
        remedy_test_channel_operation_history hist{}; bool hist_ok = false;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
        if (instance_ok && inst_id != 0) hist_ok = remedy_test_channel_get_operation_history(tok, 1, inst_id, &hist);
#endif
        publish_operation_completion_from_history(test_thread_kind::CONNECT, res, instance_ok, inst_id, hist_ok, hist);
    }

    void run_reader_thread_wrapper(remedy_channel_token_t tok, remedy_wire_frame_header_t* out_h, void* buf, size_t len, uint64_t cookie) {
        { std::lock_guard<std::mutex> lock(thread_state_mutex); reader_thread_started = true; }
        remedy_test_channel_set_operation_call_cookie(cookie);
        remedy_err_t res = channel_port_read_frame(tok, out_h, buf, len);
        uint64_t inst_id = 0;
        bool instance_ok = remedy_test_channel_take_last_created_operation_instance(&inst_id);
        remedy_test_channel_operation_history hist{}; bool hist_ok = false;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
        if (instance_ok && inst_id != 0) hist_ok = remedy_test_channel_get_operation_history(tok, 2, inst_id, &hist);
#endif
        publish_operation_completion_from_history(test_thread_kind::READER, res, instance_ok, inst_id, hist_ok, hist);
    }

    void run_writer_thread_wrapper(remedy_channel_token_t tok, const remedy_wire_frame_header_t* h, const void* payload, uint64_t cookie) {
        { std::lock_guard<std::mutex> lock(thread_state_mutex); writer_thread_started = true; }
        remedy_test_channel_set_operation_call_cookie(cookie);
        remedy_err_t res = channel_port_send_frame(tok, h, payload);
        uint64_t inst_id = 0;
        bool instance_ok = remedy_test_channel_take_last_created_operation_instance(&inst_id);
        remedy_test_channel_operation_history hist{}; bool hist_ok = false;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
        if (instance_ok && inst_id != 0) hist_ok = remedy_test_channel_get_operation_history(tok, 3, inst_id, &hist);
#endif
        publish_operation_completion_from_history(test_thread_kind::WRITER, res, instance_ok, inst_id, hist_ok, hist);
    }

    void publish_operation_completion_from_history(
        test_thread_kind kind, remedy_err_t local_res, bool instance_ok, uint64_t inst_id, bool hist_ok, const remedy_test_channel_operation_history& hist
    ) {
        std::lock_guard<std::mutex> lock(thread_state_mutex);

        auto append_error_locked = [this](const char* msg) {
            if (primary_error.empty()) primary_error = msg;
            else { primary_error += "; "; primary_error += msg; }
        };

        if (!instance_ok || inst_id == 0) {
            append_error_locked("Failed to retrieve valid operation instance ID");
        }
        if (!hist_ok) {
            append_error_locked("Operation history lookup failed");
        } else {
            if (!hist.found) append_error_locked("Operation history not found");
            if (!hist.created) append_error_locked("Operation history created flag is false");
            if (!hist.public_call_completed) append_error_locked("Operation history public_call_completed flag is false");
            if (local_res != hist.final_public_result) append_error_locked("API result != history final_public_result");

            if (hist.io_was_pending && !hist.io_submitted) append_error_locked("History tuple invalid: io_was_pending but !io_submitted");
            if (hist.kernel_completion_observed && !hist.io_submitted) append_error_locked("History tuple invalid: kernel_completion_observed but !io_submitted");
            if (hist.detached_recovered && !hist.detached_at_public_return) append_error_locked("History tuple invalid: detached_recovered but !detached_at_public_return");
            if (hist.detached_recovered && !hist.kernel_completion_observed) append_error_locked("History tuple invalid: detached_recovered but !kernel_completion_observed");
            if (hist.detached_at_public_return && !hist.io_submitted) append_error_locked("History tuple invalid: detached_at_public_return but !io_submitted");
            if (hist.detached_at_public_return && !hist.io_was_pending) append_error_locked("History tuple invalid: detached_at_public_return but !io_was_pending");
            if (hist.detached_at_public_return && hist.no_kernel_request_outstanding_at_public_return) append_error_locked("History tuple invalid: detached_at_public_return but no_kernel_request_outstanding");
            if (!hist.detached_at_public_return && !hist.no_kernel_request_outstanding_at_public_return) append_error_locked("History tuple invalid: !detached_at_public_return but kernel request outstanding");
        }

        if (kind == test_thread_kind::CONNECT) {
            connect_public_call_completed = true; connect_instance_id = inst_id; connect_operation_result = local_res;
        } else if (kind == test_thread_kind::READER) {
            reader_public_call_completed = true; reader_instance_id = inst_id; reader_operation_result = local_res;
        } else if (kind == test_thread_kind::WRITER) {
            writer_public_call_completed = true; writer_instance_id = inst_id; writer_operation_result = local_res;
        }
        thread_state_cv.notify_all();
    }

    bool wait_for_thread_completion(test_thread_kind kind, uint32_t timeout_ms) {
        std::unique_lock<std::mutex> lock(thread_state_mutex);
        return thread_state_cv.wait_for(lock, std::chrono::milliseconds(timeout_ms), [this, kind]() {
            if (kind == test_thread_kind::CONNECT) return connect_public_call_completed;
            if (kind == test_thread_kind::READER) return reader_public_call_completed;
            if (kind == test_thread_kind::WRITER) return writer_public_call_completed;
            if (kind == test_thread_kind::CLOSE) return close_public_call_completed;
            if (kind == test_thread_kind::DESTROY_A) return destroy_a_public_call_completed;
            if (kind == test_thread_kind::DESTROY_B) return destroy_b_public_call_completed;
            return false;
        });
    }

    bool destroy_endpoint_with_documented_retries(remedy_channel_token_t tok, uint64_t cookie);

    bool execute_cleanup() {
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
        remedy_test_channel_release_lease_pause();
        remedy_test_channel_release_submission_pause();
        remedy_test_channel_release_finalizer_pause();
        remedy_test_channel_release_close_pause();
#endif
        if (server_token != REMEDY_INVALID_CHANNEL_TOKEN) channel_port_close(server_token);
        if (client_token != REMEDY_INVALID_CHANNEL_TOKEN) channel_port_close(client_token);

        if (close_thread.joinable()) {
            if (!wait_for_thread_completion(
                    test_thread_kind::CLOSE,
                    2000)) {
                remedy_test_catastrophic_harness_failure(
                    "Close thread did not complete during bounded cleanup");
            }

            close_thread.join();
            close_thread_joined = true;
        }

        if (connect_thread.joinable()) {
            if (!wait_for_thread_completion(
                    test_thread_kind::CONNECT,
                    6000)) {
                remedy_test_catastrophic_harness_failure(
                    "Connect thread did not complete during bounded cleanup");
            }

            connect_thread.join();
            connect_thread_joined = true;
        }

        if (reader_thread.joinable()) {
            if (!wait_for_thread_completion(
                    test_thread_kind::READER,
                    11000)) {
                remedy_test_catastrophic_harness_failure(
                    "Reader thread did not complete during bounded cleanup");
            }

            reader_thread.join();
            reader_thread_joined = true;
        }

        if (writer_thread.joinable()) {
            if (!wait_for_thread_completion(
                    test_thread_kind::WRITER,
                    6000)) {
                remedy_test_catastrophic_harness_failure(
                    "Writer thread did not complete during bounded cleanup");
            }

            writer_thread.join();
            writer_thread_joined = true;
        }

        if (server_token != REMEDY_INVALID_CHANNEL_TOKEN && !server_destroy_succeeded) {
            server_destroy_succeeded = destroy_endpoint_with_documented_retries(server_token, destroy_a_cookie);
        }
        if (client_token != REMEDY_INVALID_CHANNEL_TOKEN && !client_destroy_succeeded) {
            client_destroy_succeeded = destroy_endpoint_with_documented_retries(client_token, destroy_a_cookie);
        }

        return primary_error.empty() && cleanup_errors.empty();
    }
};

bool test_channel_context_t::destroy_endpoint_with_documented_retries(remedy_channel_token_t tok, uint64_t cookie) {
    if (tok == REMEDY_INVALID_CHANNEL_TOKEN) return true;
    for (int attempt = 0; attempt < 3; ++attempt) {
        remedy_test_channel_set_destroy_call_cookie(cookie);
        remedy_err_t err = channel_port_destroy(tok);
        if (err == REMEDY_OK) {
            if (tok == server_token) server_destroy_succeeded = true;
            if (tok == client_token) client_destroy_succeeded = true;
            return true;
        }
        if (err != REMEDY_ERR_IPC_FAILURE) break;
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    return false;
}

void run_close_thread_wrapper(
    test_channel_context_t* ctx,
    remedy_channel_token_t tok)
{
    {
        std::lock_guard<std::mutex> lock(
            ctx->thread_state_mutex);
        ctx->close_thread_started = true;
    }

    remedy_err_t res = channel_port_close(tok);

    {
        std::lock_guard<std::mutex> lock(
            ctx->thread_state_mutex);
        ctx->close_operation_result = res;
        ctx->close_public_call_completed = true;
        ctx->thread_state_cv.notify_all();
    }
}

void run_destroy_thread_a_wrapper(test_channel_context_t* ctx, remedy_channel_token_t tok, uint64_t cookie) {
    { std::lock_guard<std::mutex> lock(ctx->thread_state_mutex); ctx->destroy_thread_a_started = true; }
    remedy_test_channel_set_destroy_call_cookie(cookie);
    remedy_err_t res = channel_port_destroy(tok);
    {
        std::lock_guard<std::mutex> lock(ctx->thread_state_mutex);
        ctx->destroy_a_public_call_completed = true; ctx->destroy_a_result = res;
        ctx->thread_state_cv.notify_all();
    }
}

void run_destroy_thread_b_wrapper(test_channel_context_t* ctx, remedy_channel_token_t tok, uint64_t cookie) {
    { std::lock_guard<std::mutex> lock(ctx->thread_state_mutex); ctx->destroy_thread_b_started = true; }
    remedy_test_channel_set_destroy_call_cookie(cookie);
    remedy_err_t res = channel_port_destroy(tok);
    {
        std::lock_guard<std::mutex> lock(ctx->thread_state_mutex);
        ctx->destroy_b_public_call_completed = true; ctx->destroy_b_result = res;
        ctx->thread_state_cv.notify_all();
    }
}

void test_normal_lifecycle(test_channel_context_t& ctx) {
    if (!ctx.create_and_connect_test_channel_pair("norm-pair", 1)) return;
    uint32_t tx_v = 0x12345678;
    remedy_wire_frame_header_t tx_h = make_test_header(sizeof(tx_v), remedy_adler32(reinterpret_cast<const uint8_t*>(&tx_v), sizeof(tx_v)));
    tx_h.magic = 0;
    tx_h.version = 0;
    tx_h.header_len = 0;
    tx_h.checksum = 0;

    ctx.writer_token = ctx.client_token;
    ctx.writer_thread = std::thread(&test_channel_context_t::run_writer_thread_wrapper, &ctx, ctx.client_token, &tx_h, &tx_v, 2);
    remedy_wire_frame_header_t rx_h{}; uint32_t rx_v = 0;
    ctx.reader_token = ctx.server_token;
    ctx.reader_thread = std::thread(&test_channel_context_t::run_reader_thread_wrapper, &ctx, ctx.server_token, &rx_h, &rx_v, sizeof(rx_v), 3);
    if (!ctx.wait_for_thread_completion(test_thread_kind::WRITER, 1000)) {
        ctx.record_primary("Writer thread timed out");
    }
    if (!ctx.wait_for_thread_completion(test_thread_kind::READER, 1000)) {
        ctx.record_primary("Reader thread timed out");
    }
    if (ctx.writer_operation_result != REMEDY_OK ||
        ctx.reader_operation_result != REMEDY_OK ||
        rx_h.magic != REMEDY_WIRE_MAGIC ||
        rx_h.version != REMEDY_WIRE_VERSION ||
        rx_h.header_len != REMEDY_WIRE_HEADER_SIZE ||
        rx_h.checksum != remedy_adler32(reinterpret_cast<const uint8_t*>(&tx_v), sizeof(tx_v)) ||
        rx_v != tx_v) {
        ctx.record_primary("Normal lifecycle assertions failed");
    }
    ctx.execute_cleanup();
}

void test_stale_token_rejection(test_channel_context_t& ctx) {
    remedy_channel_token_t stale_tok = 999999;
    if (channel_port_connect(stale_tok, 100) != REMEDY_ERR_INVALID_ARGUMENT) ctx.record_primary("Stale connect failed");
    if (channel_port_close(stale_tok) != REMEDY_ERR_INVALID_ARGUMENT) ctx.record_primary("Stale close failed");
    if (channel_port_destroy(stale_tok) != REMEDY_ERR_INVALID_ARGUMENT) ctx.record_primary("Stale destroy failed");
    ctx.execute_cleanup();
}

void test_no_new_ops_after_closing(test_channel_context_t& ctx) {
    remedy_channel_config_t cfg{"no-new-op", true};
    if (channel_port_create(&cfg, &ctx.server_token) != REMEDY_OK) {
        ctx.record_primary("Endpoint creation failed");
        return;
    }
    ctx.server_created = true;
    channel_port_close(ctx.server_token);
    if (channel_port_connect(ctx.server_token, 100) != REMEDY_ERR_REVOKING) ctx.record_primary("Connect allowed after closing");
    ctx.execute_cleanup();
}

void test_preexisting_lease_drains_before_destroy(
    test_channel_context_t& ctx)
{
    constexpr uint64_t operation_cookie = 11;
    constexpr uint64_t destroy_cookie = 101;

    remedy_channel_config_t cfg{"lease-drain", true};
    if (channel_port_create(
            &cfg,
            &ctx.server_token) != REMEDY_OK) {
        ctx.record_primary(
            "Lease-drain endpoint creation failed");
        return;
    }

    ctx.server_created = true;
    ctx.connect_token = ctx.server_token;

    if (!remedy_test_channel_arm_lease_pause(
            ctx.server_token)) {
        remedy_test_catastrophic_harness_failure(
            "Failed to arm lease-drain pause");
    }

    ctx.connect_thread = std::thread(
        &test_channel_context_t::run_connect_thread_wrapper,
        &ctx,
        ctx.server_token,
        5000,
        operation_cookie);

    if (!remedy_test_channel_wait_lease_paused(
            ctx.server_token,
            1000)) {
        remedy_test_catastrophic_harness_failure(
            "Operation did not pause while holding lease");
    }

    int state_before = 0;
    uint32_t leases_before = 0;
    uint32_t close_pins_before = 0;
    bool finalizer_before = false;
    bool in_registry_before = false;
    bool pipe_null_before = false;
    uint32_t retained_before = 0;

    if (!remedy_test_channel_get_entry_snapshot(
            ctx.server_token,
            &state_before,
            &leases_before,
            &close_pins_before,
            &finalizer_before,
            &in_registry_before,
            &pipe_null_before,
            &retained_before)) {
        remedy_test_catastrophic_harness_failure(
            "Missing lease-drain entry snapshot");
    }

    if (leases_before != 1 ||
        close_pins_before != 0 ||
        finalizer_before ||
        !in_registry_before ||
        pipe_null_before) {
        remedy_test_catastrophic_harness_failure(
            "Lease-drain pre-destroy snapshot was invalid");
    }

    ctx.destroy_thread_a = std::thread(
        run_destroy_thread_a_wrapper,
        &ctx,
        ctx.server_token,
        destroy_cookie);

    if (!remedy_test_channel_wait_finalizer_draining(
            ctx.server_token,
            1000)) {
        remedy_test_catastrophic_harness_failure(
            "Destroy did not enter lease drainage");
    }

    if (ctx.wait_for_thread_completion(
            test_thread_kind::DESTROY_A,
            50)) {
        remedy_test_catastrophic_harness_failure(
            "Destroy completed while preexisting lease was held");
    }

    if (!remedy_test_channel_release_lease_pause()) {
        remedy_test_catastrophic_harness_failure(
            "Failed to release lease-drain pause");
    }

    if (!ctx.wait_for_thread_completion(
            test_thread_kind::CONNECT,
            2000)) {
        remedy_test_catastrophic_harness_failure(
            "Lease-holding operation did not complete after release");
    }

    if (!ctx.wait_for_thread_completion(
            test_thread_kind::DESTROY_A,
            2000)) {
        remedy_test_catastrophic_harness_failure(
            "Destroy did not complete after lease drainage");
    }

    if (ctx.connect_thread.joinable()) {
        ctx.connect_thread.join();
        ctx.connect_thread_joined = true;
    }

    if (ctx.destroy_thread_a.joinable()) {
        ctx.destroy_thread_a.join();
        ctx.destroy_thread_a_joined = true;
    }

    if (ctx.connect_operation_result !=
            REMEDY_ERR_IPC_FAILURE) {
        ctx.record_primary(
            "Lease-holder did not observe rundown rejection");
    }

    if (ctx.destroy_a_result != REMEDY_OK) {
        ctx.record_primary(
            "Destroy failed after preexisting lease drained");
    } else {
        ctx.server_destroy_succeeded = true;
    }

    ctx.execute_cleanup();
}

void test_close_unblocks_pending_read(test_channel_context_t& ctx) {
    if (!ctx.create_and_connect_test_channel_pair("unblock-read", 1)) return;
    remedy_wire_frame_header_t rx_h{}; uint32_t rx_v = 0; uint64_t r_cookie = 2; uint64_t r_inst = 0;
    ctx.reader_thread = std::thread(&test_channel_context_t::run_reader_thread_wrapper, &ctx, ctx.server_token, &rx_h, &rx_v, sizeof(rx_v), r_cookie);
    if (!remedy_test_channel_wait_operation_created(ctx.server_token, 2, r_cookie, 1000, &r_inst)) {
        remedy_test_catastrophic_harness_failure("Reader operation not created");
    }
    if (!remedy_test_channel_wait_operation_pending(ctx.server_token, 2, r_inst, 1000)) {
        remedy_test_catastrophic_harness_failure("Reader operation not pending");
    }
    channel_port_close(ctx.server_token);
    ctx.execute_cleanup();
}

void test_close_racing_with_destroy_seam(
    test_channel_context_t& ctx)
{
    constexpr uint64_t destroy_cookie = 101;

    remedy_channel_config_t cfg{"close-race", true};
    if (channel_port_create(
            &cfg,
            &ctx.server_token) != REMEDY_OK) {
        ctx.record_primary(
            "Close-race endpoint creation failed");
        return;
    }

    ctx.server_created = true;

    if (!remedy_test_channel_arm_close_pause(
            ctx.server_token)) {
        remedy_test_catastrophic_harness_failure(
            "Failed to arm close-race pause");
    }

    ctx.close_thread = std::thread(
        run_close_thread_wrapper,
        &ctx,
        ctx.server_token);

    if (!remedy_test_channel_wait_close_paused(
            ctx.server_token,
            1000)) {
        remedy_test_catastrophic_harness_failure(
            "Close worker did not reach close pause");
    }

    int state_before = 0;
    uint32_t leases_before = 0;
    uint32_t close_pins_before = 0;
    bool finalizer_before = false;
    bool in_registry_before = false;
    bool pipe_null_before = false;
    uint32_t retained_before = 0;

    if (!remedy_test_channel_get_entry_snapshot(
            ctx.server_token,
            &state_before,
            &leases_before,
            &close_pins_before,
            &finalizer_before,
            &in_registry_before,
            &pipe_null_before,
            &retained_before)) {
        remedy_test_catastrophic_harness_failure(
            "Missing close-race paused snapshot");
    }

    if (leases_before != 0 ||
        close_pins_before != 1 ||
        finalizer_before ||
        !in_registry_before ||
        pipe_null_before) {
        remedy_test_catastrophic_harness_failure(
            "Close-race paused snapshot was invalid");
    }

    ctx.destroy_thread_a = std::thread(
        run_destroy_thread_a_wrapper,
        &ctx,
        ctx.server_token,
        destroy_cookie);

    if (!remedy_test_channel_wait_finalizer_draining(
            ctx.server_token,
            1000)) {
        remedy_test_catastrophic_harness_failure(
            "Destroy did not enter close-pin drainage");
    }

    int state_during = 0;
    uint32_t leases_during = 0;
    uint32_t close_pins_during = 0;
    bool finalizer_during = false;
    bool in_registry_during = false;
    bool pipe_null_during = false;
    uint32_t retained_during = 0;

    if (!remedy_test_channel_get_entry_snapshot(
            ctx.server_token,
            &state_during,
            &leases_during,
            &close_pins_during,
            &finalizer_during,
            &in_registry_during,
            &pipe_null_during,
            &retained_during)) {
        remedy_test_catastrophic_harness_failure(
            "Missing close-race drainage snapshot");
    }

    if (leases_during != 0 ||
        close_pins_during != 1 ||
        !finalizer_during ||
        !in_registry_during ||
        pipe_null_during) {
        remedy_test_catastrophic_harness_failure(
            "Destroy did not remain blocked by close pin");
    }

    if (ctx.wait_for_thread_completion(
            test_thread_kind::CLOSE,
            50)) {
        remedy_test_catastrophic_harness_failure(
            "Close completed while close pause was armed");
    }

    if (ctx.wait_for_thread_completion(
            test_thread_kind::DESTROY_A,
            50)) {
        remedy_test_catastrophic_harness_failure(
            "Destroy completed while close pin was held");
    }

    if (!remedy_test_channel_release_close_pause()) {
        remedy_test_catastrophic_harness_failure(
            "Failed to release close-race pause");
    }

    if (!ctx.wait_for_thread_completion(
            test_thread_kind::CLOSE,
            2000)) {
        remedy_test_catastrophic_harness_failure(
            "Close worker did not complete after release");
    }

    if (!ctx.wait_for_thread_completion(
            test_thread_kind::DESTROY_A,
            2000)) {
        remedy_test_catastrophic_harness_failure(
            "Destroy did not complete after close-pin release");
    }

    if (ctx.close_thread.joinable()) {
        ctx.close_thread.join();
        ctx.close_thread_joined = true;
    }

    if (ctx.destroy_thread_a.joinable()) {
        ctx.destroy_thread_a.join();
        ctx.destroy_thread_a_joined = true;
    }

    if (ctx.close_operation_result != REMEDY_OK) {
        ctx.record_primary(
            "Concurrent close returned a non-OK result");
    } else {
        ctx.server_close_succeeded = true;
    }

    if (ctx.destroy_a_result != REMEDY_OK) {
        ctx.record_primary(
            "Destroy failed after close-pin drainage");
    } else {
        ctx.server_destroy_succeeded = true;
    }

    ctx.execute_cleanup();
}

void test_synchronized_concurrent_destroy(
    test_channel_context_t& ctx)
{
    constexpr uint64_t owner_cookie = 101;
    constexpr uint64_t competitor_cookie = 102;

    remedy_channel_config_t cfg{"sync-destroy", true};
    if (channel_port_create(
            &cfg,
            &ctx.server_token) != REMEDY_OK) {
        ctx.record_primary(
            "Concurrent destroy endpoint creation failed");
        return;
    }

    if (!remedy_test_channel_arm_finalizer_pause(
            ctx.server_token)) {
        remedy_test_catastrophic_harness_failure(
            "Failed to arm concurrent destroy finalizer pause");
    }

    ctx.destroy_thread_a = std::thread(
        run_destroy_thread_a_wrapper,
        &ctx,
        ctx.server_token,
        owner_cookie);

    if (!remedy_test_channel_wait_finalizer_paused(
            ctx.server_token,
            1000)) {
        remedy_test_catastrophic_harness_failure(
            "Destroy owner did not reach finalizer pause");
    }

    channel_finalizer_owner_observation owner_before{};
    if (!remedy_test_channel_get_finalizer_owner_observation(
            ctx.server_token,
            &owner_before)) {
        remedy_test_catastrophic_harness_failure(
            "Missing initial finalizer owner observation");
    }

    if (owner_before.token != ctx.server_token ||
        owner_before.generation_number == 0 ||
        owner_before.owner_call_cookie != owner_cookie ||
        !owner_before.finalizer_active ||
        owner_before.draining ||
        owner_before.public_destroy_completed ||
        owner_before.finalizer_result != REMEDY_OK ||
        !owner_before.entry_remains_registered) {
        remedy_test_catastrophic_harness_failure(
            "Invalid initial finalizer owner observation");
    }

    ctx.destroy_thread_b = std::thread(
        run_destroy_thread_b_wrapper,
        &ctx,
        ctx.server_token,
        competitor_cookie);

    uint64_t observed_competitor_cookie = 0;
    if (!remedy_test_channel_wait_competitor_attached(
            ctx.server_token,
            owner_before.generation_number,
            1000,
            &observed_competitor_cookie)) {
        remedy_test_catastrophic_harness_failure(
            "Destroy competitor did not attach to owner generation");
    }

    if (observed_competitor_cookie != competitor_cookie) {
        remedy_test_catastrophic_harness_failure(
            "Wrong destroy competitor attached to generation");
    }

    if (ctx.wait_for_thread_completion(
            test_thread_kind::DESTROY_A,
            50)) {
        remedy_test_catastrophic_harness_failure(
            "Destroy owner completed while finalizer was paused");
    }

    if (ctx.wait_for_thread_completion(
            test_thread_kind::DESTROY_B,
            50)) {
        remedy_test_catastrophic_harness_failure(
            "Destroy competitor completed before owner generation");
    }

    if (!remedy_test_channel_release_finalizer_pause()) {
        remedy_test_catastrophic_harness_failure(
            "Failed to release concurrent destroy finalizer pause");
    }

    if (!ctx.wait_for_thread_completion(
            test_thread_kind::DESTROY_A,
            2000)) {
        remedy_test_catastrophic_harness_failure(
            "Destroy owner did not complete after pause release");
    }

    if (!ctx.wait_for_thread_completion(
            test_thread_kind::DESTROY_B,
            2000)) {
        remedy_test_catastrophic_harness_failure(
            "Destroy competitor did not receive generation result");
    }

    if (ctx.destroy_thread_a.joinable()) {
        ctx.destroy_thread_a.join();
        ctx.destroy_thread_a_joined = true;
    }

    if (ctx.destroy_thread_b.joinable()) {
        ctx.destroy_thread_b.join();
        ctx.destroy_thread_b_joined = true;
    }

    if (ctx.destroy_a_result != REMEDY_OK ||
        ctx.destroy_b_result != REMEDY_OK) {
        ctx.record_primary(
            "Concurrent destroy calls did not share successful result");
    } else {
        ctx.server_destroy_succeeded = true;
    }

    channel_finalizer_owner_observation owner_after{};
    if (!remedy_test_channel_get_finalizer_owner_observation(
            ctx.server_token,
            &owner_after)) {
        remedy_test_catastrophic_harness_failure(
            "Missing completed finalizer owner observation");
    }

    if (owner_after.token != ctx.server_token ||
        owner_after.generation_number !=
            owner_before.generation_number ||
        owner_after.owner_call_cookie != owner_cookie ||
        owner_after.finalizer_active ||
        owner_after.draining ||
        !owner_after.public_destroy_completed ||
        owner_after.finalizer_result != REMEDY_OK ||
        owner_after.entry_remains_registered) {
        ctx.record_primary(
            "Completed finalizer generation observation was invalid");
    }

    ctx.execute_cleanup();
}

void test_peer_endpoint_destroyed_during_pending_read(test_channel_context_t& ctx) {
    if (!ctx.create_and_connect_test_channel_pair("peer-dest-read", 1)) return;
    remedy_wire_frame_header_t rx_h{}; uint32_t rx_v = 0; uint64_t r_cookie = 2; uint64_t r_inst = 0;
    ctx.reader_thread = std::thread(&test_channel_context_t::run_reader_thread_wrapper, &ctx, ctx.server_token, &rx_h, &rx_v, sizeof(rx_v), r_cookie);
    if (!remedy_test_channel_wait_operation_created(ctx.server_token, 2, r_cookie, 1000, &r_inst)) {
        remedy_test_catastrophic_harness_failure("Reader operation not created");
    }
    if (!remedy_test_channel_wait_operation_pending(ctx.server_token, 2, r_inst, 1000)) {
        remedy_test_catastrophic_harness_failure("Reader operation not pending");
    }
    channel_port_close(ctx.client_token);
    if (!ctx.destroy_endpoint_with_documented_retries(ctx.client_token, 101)) {
        ctx.record_primary("Client destroy failed");
    }
    ctx.execute_cleanup();
}

void test_close_followed_by_destroy(test_channel_context_t& ctx) {
    remedy_channel_config_t cfg{"close-dest", true};
    if (channel_port_create(&cfg, &ctx.server_token) != REMEDY_OK) {
        ctx.record_primary("Endpoint creation failed");
        return;
    }
    ctx.server_created = true;
    channel_port_close(ctx.server_token);
    if (!ctx.destroy_endpoint_with_documented_retries(ctx.server_token, 101)) {
        ctx.record_primary("Destroy failed");
    }
    ctx.execute_cleanup();
}

void test_failed_partial_construction(test_channel_context_t& ctx) {
    uint32_t entries_before = 0, deleted_before = 0;
    if (!remedy_test_channel_get_final_counts(&entries_before, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, &deleted_before, NULL, NULL, NULL, NULL, NULL, NULL, NULL)) {
        remedy_test_catastrophic_harness_failure("Failed to get initial counts");
    }

    if (!remedy_test_channel_inject_pipe_creation_failure(ERROR_OUTOFMEMORY)) {
        remedy_test_catastrophic_harness_failure("Failed to inject pipe creation failure");
    }
    remedy_channel_config_t cfg{"part-create", true}; remedy_channel_token_t tok = 0;
    remedy_err_t res = channel_port_create(&cfg, &tok);

    uint32_t entries_after = 0, deleted_after = 0;
    if (!remedy_test_channel_get_final_counts(&entries_after, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, &deleted_after, NULL, NULL, NULL, NULL, NULL, NULL, NULL)) {
        remedy_test_catastrophic_harness_failure("Failed to get post-injection counts");
    }

    if (res != REMEDY_ERR_IPC_FAILURE || tok != REMEDY_INVALID_CHANNEL_TOKEN || entries_after != entries_before || deleted_after != deleted_before + 1) {
        ctx.record_primary("Partial construction assertions failed");
    }
    ctx.execute_cleanup();
}

void test_token_wraparound_collision(test_channel_context_t& ctx) {
    remedy_channel_config_t s_cfg1{"wrap-s1", true};
    if (channel_port_create(&s_cfg1, &ctx.server_token) != REMEDY_OK) {
        ctx.record_primary("Server 1 creation failed in wraparound scenario");
        return;
    }
    ctx.server_created = true;

    {
        token_override_guard guard(ctx.server_token, ctx);
        remedy_channel_config_t s_cfg2{"wrap-s2", true};
        if (channel_port_create(&s_cfg2, &ctx.client_token) != REMEDY_OK) {
            ctx.record_primary("Server 2 creation failed in wraparound scenario");
            ctx.execute_cleanup();
            return;
        }
        ctx.client_created = true;
        if (ctx.client_token == ctx.server_token || ctx.client_token == REMEDY_INVALID_CHANNEL_TOKEN) {
            ctx.record_primary("Collision resolution failed");
        }
    }

    {
        remedy_channel_token_t max_tok = UINT64_MAX - 1;
        token_override_guard guard(max_tok, ctx);
        remedy_channel_config_t wrap_cfg1{"wrap-t1", true}; remedy_channel_token_t tok1 = 0;
        if (channel_port_create(&wrap_cfg1, &tok1) != REMEDY_OK) {
            ctx.record_primary("Temp token 1 creation failed in wraparound scenario");
        }
        remedy_channel_config_t wrap_cfg2{"wrap-t2", true}; remedy_channel_token_t tok2 = 0;
        if (channel_port_create(&wrap_cfg2, &tok2) != REMEDY_OK) {
            ctx.record_primary("Temp token 2 creation failed in wraparound scenario");
        }

        if (tok1 != max_tok || tok2 == REMEDY_INVALID_CHANNEL_TOKEN || tok2 == 0) {
            ctx.record_primary("Wraparound failed");
        }

        if (tok1 != 0 && tok1 != REMEDY_INVALID_CHANNEL_TOKEN) {
            channel_port_close(tok1);
            if (!ctx.destroy_endpoint_with_documented_retries(tok1, 901)) {
                ctx.record_primary("Temp token 1 destroy failed in wraparound scenario");
            }
        }
        if (tok2 != 0 && tok2 != REMEDY_INVALID_CHANNEL_TOKEN) {
            channel_port_close(tok2);
            if (!ctx.destroy_endpoint_with_documented_retries(tok2, 902)) {
                ctx.record_primary("Temp token 2 destroy failed in wraparound scenario");
            }
        }
    }
    ctx.execute_cleanup();
}

void test_event_close_failure_retries(test_channel_context_t& ctx) {
    if (!ctx.create_and_connect_test_channel_pair("evt-retry-pair", 1)) return;
    remedy_wire_frame_header_t rx_h{}; uint32_t rx_v = 0; uint64_t r_cookie = 2; uint64_t r_inst = 0;
    ctx.reader_thread = std::thread(&test_channel_context_t::run_reader_thread_wrapper, &ctx, ctx.server_token, &rx_h, &rx_v, sizeof(rx_v), r_cookie);
    if (!remedy_test_channel_wait_operation_created(ctx.server_token, 2, r_cookie, 1000, &r_inst)) {
        remedy_test_catastrophic_harness_failure("Reader operation not created");
    }
    if (!remedy_test_channel_wait_operation_pending(ctx.server_token, 2, r_inst, 1000)) {
        remedy_test_catastrophic_harness_failure("Reader operation not pending");
    }
    if (!remedy_test_channel_inject_event_close_failure(ctx.server_token, 2)) {
        remedy_test_catastrophic_harness_failure("Failed to inject event close failure");
    }
    channel_port_close(ctx.server_token);
    remedy_test_channel_set_destroy_call_cookie(101);
    remedy_err_t first_err = channel_port_destroy(ctx.server_token);
    if (first_err != REMEDY_ERR_IPC_FAILURE) ctx.record_primary("First destroy did not return REMEDY_ERR_IPC_FAILURE");
    if (!ctx.destroy_endpoint_with_documented_retries(ctx.server_token, 102)) {
        ctx.record_primary("Destroy retry failed");
    }
    ctx.execute_cleanup();
}

void test_cancellation_failure_retains_entry(test_channel_context_t& ctx) {
    if (!ctx.create_and_connect_test_channel_pair("cnc-fail-pair", 1)) return;
    remedy_wire_frame_header_t rx_h{}; uint32_t rx_v = 0; uint64_t r_cookie = 2; uint64_t r_inst = 0;
    ctx.reader_thread = std::thread(&test_channel_context_t::run_reader_thread_wrapper, &ctx, ctx.server_token, &rx_h, &rx_v, sizeof(rx_v), r_cookie);
    if (!remedy_test_channel_wait_operation_created(ctx.server_token, 2, r_cookie, 1000, &r_inst)) {
        remedy_test_catastrophic_harness_failure("Reader operation not created");
    }
    if (!remedy_test_channel_wait_operation_pending(ctx.server_token, 2, r_inst, 1000)) {
        remedy_test_catastrophic_harness_failure("Reader operation not pending");
    }
    if (!remedy_test_channel_inject_operation_cancel_failure(ctx.server_token, 2, ERROR_GEN_FAILURE)) {
        remedy_test_catastrophic_harness_failure("Failed to inject operation cancel failure");
    }
    channel_port_close(ctx.server_token);
    remedy_test_channel_set_destroy_call_cookie(101);
    remedy_err_t first_err = channel_port_destroy(ctx.server_token);
    if (first_err != REMEDY_ERR_IPC_FAILURE) ctx.record_primary("First destroy did not return REMEDY_ERR_IPC_FAILURE");
    if (!ctx.destroy_endpoint_with_documented_retries(ctx.server_token, 102)) {
        ctx.record_primary("Destroy retry failed");
    }
    ctx.execute_cleanup();
}

void test_failed_pipe_close_retains_entry(test_channel_context_t& ctx) {
    remedy_channel_config_t cfg{"fail-pipe-cls", true};
    if (channel_port_create(&cfg, &ctx.server_token) != REMEDY_OK) {
        ctx.record_primary("Endpoint creation failed");
        return;
    }
    ctx.server_created = true;
    if (!remedy_test_channel_inject_pipe_close_failure(ctx.server_token)) {
        remedy_test_catastrophic_harness_failure("Failed to inject pipe close failure");
    }
    channel_port_close(ctx.server_token);
    if (!ctx.destroy_endpoint_with_documented_retries(ctx.server_token, 101)) {
        ctx.record_primary("Destroy retry failed");
    }
    ctx.execute_cleanup();
}

void test_operation_cancel_failure_detaches_safely(test_channel_context_t& ctx) {
    if (!ctx.create_and_connect_test_channel_pair("op-cnc-detach", 1)) return;
    remedy_wire_frame_header_t rx_h{}; uint32_t rx_v = 0; uint64_t r_cookie = 2; uint64_t r_inst = 0;
    ctx.reader_thread = std::thread(&test_channel_context_t::run_reader_thread_wrapper, &ctx, ctx.server_token, &rx_h, &rx_v, sizeof(rx_v), r_cookie);
    if (!remedy_test_channel_wait_operation_created(ctx.server_token, 2, r_cookie, 1000, &r_inst)) {
        remedy_test_catastrophic_harness_failure("Reader operation not created");
    }
    if (!remedy_test_channel_wait_operation_pending(ctx.server_token, 2, r_inst, 1000)) {
        remedy_test_catastrophic_harness_failure("Reader operation not pending");
    }
    if (!remedy_test_channel_inject_operation_cancel_failure(ctx.server_token, 2, ERROR_GEN_FAILURE)) {
        remedy_test_catastrophic_harness_failure("Failed to inject operation cancel failure");
    }
    channel_port_close(ctx.server_token);
    if (!ctx.destroy_endpoint_with_documented_retries(ctx.server_token, 102)) {
        ctx.record_primary("Destroy retry failed");
    }
    ctx.execute_cleanup();
}

void test_atomic_submission_admission_rejection(test_channel_context_t& ctx) {
    remedy_channel_config_t cfg{"admit-rej", true};
    if (channel_port_create(&cfg, &ctx.server_token) != REMEDY_OK) {
        ctx.record_primary("Endpoint creation failed");
        return;
    }
    ctx.server_created = true;
    channel_port_close(ctx.server_token);
    uint32_t tx_v = 1;
    remedy_wire_frame_header_t tx_h = make_test_header(sizeof(tx_v), remedy_adler32(reinterpret_cast<const uint8_t*>(&tx_v), sizeof(tx_v)));
    if (channel_port_send_frame(ctx.server_token, &tx_h, &tx_v) != REMEDY_ERR_REVOKING) ctx.record_primary("Admission rejection failed");
    ctx.execute_cleanup();
}

void test_handle_leak_verification(test_channel_context_t& ctx) {
    if (!ctx.create_and_connect_test_channel_pair("leak-warmup", 1)) return;
    channel_port_close(ctx.server_token); channel_port_close(ctx.client_token);
    if (!ctx.destroy_endpoint_with_documented_retries(ctx.server_token, 101)) {
        ctx.record_primary("Server destroy failed during warmup");
    }
    if (!ctx.destroy_endpoint_with_documented_retries(ctx.client_token, 102)) {
        ctx.record_primary("Client destroy failed during warmup");
    }
    ctx.server_token = REMEDY_INVALID_CHANNEL_TOKEN; ctx.client_token = REMEDY_INVALID_CHANNEL_TOKEN;

    DWORD baseline_handles = 0;
    if (!GetProcessHandleCount(GetCurrentProcess(), &baseline_handles)) {
        remedy_test_catastrophic_harness_failure("GetProcessHandleCount baseline failed");
    }

    for (int i = 0; i < 100; ++i) {
        char s_name[64]; snprintf(s_name, sizeof(s_name), "leak-%d", i);
        test_channel_context_t iter_ctx;
        if (iter_ctx.create_and_connect_test_channel_pair(s_name, 1000 + i)) {
            uint32_t tx_v = 0xAA000000 | i;
            remedy_wire_frame_header_t tx_h = make_test_header(sizeof(tx_v), remedy_adler32(reinterpret_cast<const uint8_t*>(&tx_v), sizeof(tx_v)));
            iter_ctx.writer_thread = std::thread(&test_channel_context_t::run_writer_thread_wrapper, &iter_ctx, iter_ctx.client_token, &tx_h, &tx_v, 2000 + i);
            remedy_wire_frame_header_t rx_h{}; uint32_t rx_v = 0;
            iter_ctx.reader_thread = std::thread(&test_channel_context_t::run_reader_thread_wrapper, &iter_ctx, iter_ctx.server_token, &rx_h, &rx_v, sizeof(rx_v), 3000 + i);
            if (!iter_ctx.wait_for_thread_completion(test_thread_kind::WRITER, 100)) {
                ctx.record_primary("Handle leak iteration writer timed out");
            }
            if (!iter_ctx.wait_for_thread_completion(test_thread_kind::READER, 100)) {
                ctx.record_primary("Handle leak iteration reader timed out");
            }
            if (rx_v != tx_v) ctx.record_primary("Handle leak iteration payload mismatch");
            channel_port_close(iter_ctx.server_token); channel_port_close(iter_ctx.client_token);
            if (!iter_ctx.destroy_endpoint_with_documented_retries(iter_ctx.server_token, 4000 + i)) {
                ctx.record_primary("Handle leak iteration server destroy failed");
            }
            if (!iter_ctx.destroy_endpoint_with_documented_retries(iter_ctx.client_token, 5000 + i)) {
                ctx.record_primary("Handle leak iteration client destroy failed");
            }
            if (!iter_ctx.execute_cleanup()) {
                ctx.record_primary("Handle leak iteration cleanup failed: " + iter_ctx.primary_error + "; " + iter_ctx.cleanup_errors);
            }
        } else {
            ctx.record_primary("Handle leak iteration create_and_connect failed: " + iter_ctx.primary_error);
        }
    }

    DWORD final_handles = 0;
    if (!GetProcessHandleCount(GetCurrentProcess(), &final_handles)) {
        remedy_test_catastrophic_harness_failure("GetProcessHandleCount final failed");
    }
    if (final_handles != baseline_handles) ctx.record_primary("Handle leak detected across 100 iterations");
    ctx.execute_cleanup();
}

bool run_all_channel_lifetime_scenarios(void) {
    bool overall_pass = true;
    if (!remedy_test_channel_reset_seam()) {
        std::fprintf(stderr, "Failed to reset seam before suite!\n");
        return false;
    }

    auto run_one = [&overall_pass](const char* name, void(*func)(test_channel_context_t&)) {
        test_channel_context_t ctx;
        func(ctx);
        if (!ctx.primary_error.empty() || !ctx.cleanup_errors.empty()) {
            std::fprintf(stderr, "SCENARIO FAILED [%s]: primary='%s', cleanup='%s'\n", name, ctx.primary_error.c_str(), ctx.cleanup_errors.c_str());
            overall_pass = false;
        }
    };

    run_one("test_normal_lifecycle", test_normal_lifecycle);
    run_one("test_stale_token_rejection", test_stale_token_rejection);
    run_one("test_no_new_ops_after_closing", test_no_new_ops_after_closing);
    run_one("test_preexisting_lease_drains_before_destroy", test_preexisting_lease_drains_before_destroy);
    run_one("test_close_unblocks_pending_read", test_close_unblocks_pending_read);
    run_one("test_close_racing_with_destroy_seam", test_close_racing_with_destroy_seam);
    run_one("test_synchronized_concurrent_destroy", test_synchronized_concurrent_destroy);
    run_one("test_peer_endpoint_destroyed_during_pending_read", test_peer_endpoint_destroyed_during_pending_read);
    run_one("test_close_followed_by_destroy", test_close_followed_by_destroy);
    run_one("test_failed_partial_construction", test_failed_partial_construction);
    run_one("test_token_wraparound_collision", test_token_wraparound_collision);
    run_one("test_event_close_failure_retries", test_event_close_failure_retries);
    run_one("test_cancellation_failure_retains_entry", test_cancellation_failure_retains_entry);
    run_one("test_failed_pipe_close_retains_entry", test_failed_pipe_close_retains_entry);
    run_one("test_operation_cancel_failure_detaches_safely", test_operation_cancel_failure_detaches_safely);
    run_one("test_atomic_submission_admission_rejection", test_atomic_submission_admission_rejection);
    run_one("test_handle_leak_verification", test_handle_leak_verification);

    uint32_t act_entries = UINT32_MAX, act_leases = UINT32_MAX, act_close_pins = UINT32_MAX;
    uint32_t ret_records = UINT32_MAX, sub_pending = UINT32_MAX, det_pending = UINT32_MAX;
    uint32_t comp_retained = UINT32_MAX, buf_alloc = UINT32_MAX, buf_freed = UINT32_MAX;
    uint32_t det_created = UINT32_MAX, det_rec = UINT32_MAX, unconsumed_inj = UINT32_MAX;
    uint32_t armed_p = UINT32_MAX, pending_ops = UINT32_MAX, fin_inv = UINT32_MAX;
    uint32_t fin_comp = UINT32_MAX, succ_fin = UINT32_MAX, entry_del = UINT32_MAX;
    uint32_t pipe_cls = UINT32_MAX, event_cls = UINT32_MAX, pipe_cls_fail = UINT32_MAX;
    uint32_t evt_cls_fail = UINT32_MAX, pipe_canc_fail = UINT32_MAX, op_canc_fail = UINT32_MAX;
    uint32_t wait_fail = UINT32_MAX;

    bool counts_ok = remedy_test_channel_get_final_counts(
        &act_entries, &act_leases, &act_close_pins, &ret_records,
        &sub_pending, &det_pending, &comp_retained, &buf_alloc,
        &buf_freed, &det_created, &det_rec, &unconsumed_inj,
        &armed_p, &pending_ops, &fin_inv, &fin_comp,
        &succ_fin, &entry_del, &pipe_cls, &event_cls,
        &pipe_cls_fail, &evt_cls_fail, &pipe_canc_fail, &op_canc_fail,
        &wait_fail
    );

    if (!counts_ok) {
        std::fprintf(stderr, "remedy_test_channel_get_final_counts returned false!\n");
        overall_pass = false;
    }

    if (act_entries != 0) { std::fprintf(stderr, "act_entries != 0: %u\n", act_entries); overall_pass = false; }
    if (act_leases != 0) { std::fprintf(stderr, "act_leases != 0: %u\n", act_leases); overall_pass = false; }
    if (act_close_pins != 0) { std::fprintf(stderr, "act_close_pins != 0: %u\n", act_close_pins); overall_pass = false; }
    if (ret_records != 0) { std::fprintf(stderr, "ret_records != 0: %u\n", ret_records); overall_pass = false; }
    if (sub_pending != 0) { std::fprintf(stderr, "sub_pending != 0: %u\n", sub_pending); overall_pass = false; }
    if (det_pending != 0) { std::fprintf(stderr, "det_pending != 0: %u\n", det_pending); overall_pass = false; }
    if (comp_retained != 0) { std::fprintf(stderr, "comp_retained != 0: %u\n", comp_retained); overall_pass = false; }
    if (unconsumed_inj != 0) { std::fprintf(stderr, "unconsumed_inj != 0: %u\n", unconsumed_inj); overall_pass = false; }
    if (armed_p != 0) { std::fprintf(stderr, "armed_p != 0: %u\n", armed_p); overall_pass = false; }
    if (pending_ops != 0) { std::fprintf(stderr, "pending_ops != 0: %u\n", pending_ops); overall_pass = false; }

    if (buf_alloc != buf_freed || buf_alloc != 207) { std::fprintf(stderr, "buf balance mismatch: %u alloc, %u freed\n", buf_alloc, buf_freed); overall_pass = false; }
    if (det_created != det_rec || det_created != 2) { std::fprintf(stderr, "det balance mismatch: %u created, %u rec\n", det_created, det_rec); overall_pass = false; }

    if (fin_inv != 230) { std::fprintf(stderr, "fin_inv != 230: %u\n", fin_inv); overall_pass = false; }
    if (fin_comp != 225) { std::fprintf(stderr, "fin_comp != 225: %u\n", fin_comp); overall_pass = false; }
    if (succ_fin != 225) { std::fprintf(stderr, "succ_fin != 225: %u\n", succ_fin); overall_pass = false; }
    if (entry_del != 226) { std::fprintf(stderr, "entry_del != 226: %u\n", entry_del); overall_pass = false; }
    if (pipe_cls != 225) { std::fprintf(stderr, "pipe_cls != 225: %u\n", pipe_cls); overall_pass = false; }
    if (event_cls != 315) { std::fprintf(stderr, "event_cls != 315: %u\n", event_cls); overall_pass = false; }
    if (pipe_cls_fail != 1) { std::fprintf(stderr, "pipe_cls_fail != 1: %u\n", pipe_cls_fail); overall_pass = false; }
    if (evt_cls_fail != 1) { std::fprintf(stderr, "evt_cls_fail != 1: %u\n", evt_cls_fail); overall_pass = false; }
    if (pipe_canc_fail != 0) { std::fprintf(stderr, "pipe_canc_fail != 0: %u\n", pipe_canc_fail); overall_pass = false; }
    if (op_canc_fail != 2) { std::fprintf(stderr, "op_canc_fail != 2: %u\n", op_canc_fail); overall_pass = false; }
    if (wait_fail != 0) { std::fprintf(stderr, "wait_fail != 0: %u\n", wait_fail); overall_pass = false; }

    return overall_pass;
}

int main(int argc, char* argv[]) {
    return run_all_channel_lifetime_scenarios() ? 0 : 1;
}

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
#include <cstdlib>
#include <cstdio>
#include <cstring>
#include <atomic>
#include <mutex>
#include <condition_variable>
#include <unordered_map>
#include <map>
#include <tuple>
#include <string>
#include <thread>
#include <chrono>
#include <memory>
#include <new>

#include "remedy/ports/channel_port.h"
#include "remedy/wire_frame.h"

enum class channel_entry_state : uint32_t { CONSTRUCTING = 1, LIVE = 2, CLOSING = 3, RETIRED = 4 };
enum class channel_operation_kind : uint32_t { CONNECT = 1, READ = 2, WRITE = 3 };
enum class channel_operation_state : uint32_t {
    ALLOCATED, LINKED, READY_TO_SUBMIT, SUBMITTED_FAILED_IMMEDIATE, SUBMITTED_IMMEDIATE,
    SUBMITTED_PENDING, CHUNK_COMPLETED, CANCEL_REQUESTED, DETACHED_PENDING, COMPLETED, RETIREMENT_FAILED, RETIRED
};
enum class channel_completion_owner : uint32_t { PUBLIC_CALL, DETACHED_FINALIZER, NONE_TERMINAL };

enum class channel_detach_transition_result { DETACHED, INVALID_INTERNAL_STATE };

enum class channel_record_retire_result { RETIRED, RETAINED_CLOSE_FAILURE, CONTAINMENT_FAILURE };
struct channel_record_retire_status {
    channel_record_retire_result result;
    DWORD win32_error;
    bool record_deleted;
};

struct channel_public_return_disposition {
    enum class kind { NO_CURRENT_REQUEST, TERMINAL_CURRENT_REQUEST, DETACHED_CURRENT_REQUEST, INVALID_OWNERSHIP };
    kind value;
};

struct channel_public_return_result {
    channel_public_return_disposition disposition;
    remedy_err_t api_result;
};

enum class channel_pending_resolution { COMPLETED_IMMEDIATE, COMPLETED_WAIT, DETACHED_PENDING, TIMED_OUT, CANCELED };
enum class channel_submit_kind { CONNECT, READ, WRITE };
enum class channel_submit_reason { IMMEDIATE_SUCCESS, PENDING, IMMEDIATE_FAILURE, ADMISSION_REJECTED, RESET_FAILED };

struct channel_submit_result {
    channel_submit_reason reason;
    DWORD win32_error;
    DWORD transferred_bytes;
    channel_operation_state new_state;
};

enum class channel_wait_reason { WAIT_REASON_COMPLETED, WAIT_REASON_TIMED_OUT, WAIT_REASON_CANCELED, WAIT_REASON_FAILED };
struct channel_wait_result {
    channel_wait_reason reason;
    DWORD win32_error;
    DWORD transferred_bytes;
};

enum class channel_completion_status { SUCCESS, IO_FAILURE, TIMED_OUT, CANCELED, DETACHED_PENDING, CONTAINMENT_FAILED };
struct channel_completion_result {
    channel_completion_status status;
    remedy_err_t api_result;
    DWORD win32_error;
    size_t transferred_bytes;
};

struct channel_cancel_result {
    bool success;
    DWORD win32_error;
    bool io_was_pending;
};

struct channel_test_counts {
    uint32_t active_entries{0}; uint32_t active_public_leases{0}; uint32_t active_close_pins{0}; uint32_t retained_records{0};
    uint32_t submitted_pending{0}; uint32_t detached_pending{0}; uint32_t completed_retained{0}; uint32_t buffers_allocated{0};
    uint32_t buffers_freed{0}; uint32_t detached_created{0}; uint32_t detached_recovered{0}; uint32_t unconsumed_injections{0};
    uint32_t armed_pauses{0}; uint32_t pending_operations{0}; uint32_t finalizer_invocations{0}; uint32_t finalizer_completions{0};
    uint32_t successful_finalizers{0}; uint32_t entry_deletions{0}; uint32_t pipe_closures{0}; uint32_t event_closures{0};
    uint32_t pipe_close_failures{0}; uint32_t event_close_failures{0}; uint32_t pipe_cancel_failures{0}; uint32_t operation_cancel_failures{0};
    uint32_t wait_failures{0}; uint32_t total_active_detachment_reservations{0};
};

static std::atomic<uint64_t> g_next_operation_instance_id{1};

struct channel_operation_record {
    OVERLAPPED overlapped{};
    HANDLE event_handle{NULL};

    channel_operation_kind kind{channel_operation_kind::READ};
    channel_operation_state state{channel_operation_state::ALLOCATED};
    channel_completion_owner completion_owner{channel_completion_owner::PUBLIC_CALL};
    uint64_t instance_id{0};
    uint64_t call_cookie{0};
    bool detachment_reserved{false};

    bool current_request_submitted{false};
    bool current_request_pending{false};
    bool current_request_completion_observed{false};

    bool ever_submitted{false};
    bool ever_pending{false};
    bool ever_completion_observed{false};
    bool detached_from_public_call{false};
    bool event_close_verified{false};
    bool no_kernel_request_outstanding_at_public_return{true};

    size_t submitted_buffer_offset{0};
    DWORD submitted_chunk_length{0};

    bool detached_at_public_return{false};
    bool detached_recovered{false};
    bool cancellation_failed{false};
    bool cancellation_failed_observed_by_destroy{false};
    bool event_close_failure_observed_by_destroy{false};
    remedy_err_t final_public_result{REMEDY_OK};
    DWORD final_completion_error{ERROR_SUCCESS};

    DWORD submission_error{ERROR_SUCCESS};
    DWORD cancellation_error{ERROR_SUCCESS};
    DWORD completion_error{ERROR_SUCCESS};

    std::byte* owned_buffer{nullptr};
    size_t owned_buffer_size{0};
    size_t requested_size{0};
    size_t transferred_size{0};
    size_t cumulative_target{0};
    size_t cumulative_transferred{0};

    channel_operation_record* next{nullptr};
};

enum class channel_transfer_status {
    COMPLETED,
    PENDING_UNRESOLVED,
    IPC_FAILURE,
    BAD_LENGTH
};

struct channel_transfer_result {
    channel_transfer_status status;
    DWORD last_error;
    size_t cumulative_transferred;
};

struct channel_operation_transaction {
    bool record_linked{false};
    bool pending_published{false};
    bool detached{false};
    remedy_err_t primary_error{REMEDY_OK};
    DWORD wait_error{ERROR_SUCCESS};
    DWORD drainage_error{ERROR_SUCCESS};
};

struct finalizer_generation_info {
    uint64_t generation_number{0};
    uint64_t owner_call_cookie{0};
    bool active{false};
    bool completed{false};
    remedy_err_t final_result{REMEDY_OK};
    bool entry_remains_registered{true};
};

struct win32_channel_entry {
    std::mutex entry_mutex;
    std::condition_variable cv;

    HANDLE pipe_handle{INVALID_HANDLE_VALUE};
    bool   is_server{false};
    channel_entry_state state{channel_entry_state::CONSTRUCTING};
    uint32_t active_leases{0};
    uint32_t close_pins{0};
    uint32_t detachment_reservations{0};
    uint32_t detached_pending_operations{0};
    bool finalizer_active{false};
    uint32_t finalizer_generation_waiters{0};
    uint64_t current_finalizer_generation{0};
    finalizer_generation_info active_finalizer_gen{};
    std::atomic<uint32_t> finalizer_ref_count{1};

    channel_operation_record* retained_events{nullptr};
};

class channel_lease_guard {
public:
    channel_lease_guard() = default;
    ~channel_lease_guard() { release(); }
    channel_lease_guard(const channel_lease_guard&) = delete;
    channel_lease_guard& operator=(const channel_lease_guard&) = delete;
    channel_lease_guard(channel_lease_guard&& o) noexcept : entry_(o.entry_) { o.entry_ = nullptr; }
    channel_lease_guard& operator=(channel_lease_guard&& o) noexcept {
        if (this != &o) { release(); entry_ = o.entry_; o.entry_ = nullptr; }
        return *this;
    }
    void release() {
        if (entry_) {
            std::lock_guard<std::mutex> lock(entry_->entry_mutex);
            if (entry_->active_leases > 0) { entry_->active_leases--; entry_->cv.notify_all(); }
            entry_ = nullptr;
        }
    }
    win32_channel_entry* get() const { return entry_; }
private:
    friend remedy_err_t channel_acquire_live_lease(remedy_channel_token_t, win32_channel_entry**, channel_lease_guard*);
    void adopt_locked(win32_channel_entry* e) { release(); entry_ = e; }
    win32_channel_entry* entry_{nullptr};
};

#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM

struct remedy_test_channel_operation_history {
    bool found{false}; bool created{false}; bool io_submitted{false}; bool io_was_pending{false};
    bool public_call_completed{false}; bool kernel_completion_observed{false};
    bool detached_at_public_return{false}; bool detached_recovered{false};
    bool no_kernel_request_outstanding_at_public_return{true};
    remedy_err_t final_public_result{REMEDY_OK}; DWORD final_completion_error{ERROR_SUCCESS};
};

struct channel_operation_history_snapshot {
    remedy_channel_token_t token{REMEDY_INVALID_CHANNEL_TOKEN}; uint32_t kind{1}; uint64_t instance_id{0};
    bool created{false}; bool io_submitted{false}; bool io_was_pending{false}; bool public_call_completed{false};
    bool kernel_completion_observed{false}; bool detached_at_public_return{false}; bool detached_recovered{false};
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
    [[noreturn]] void remedy_test_catastrophic_harness_failure(const char* message);
    bool remedy_test_channel_reset_seam(void);
    bool remedy_test_channel_record_history_snapshot(const channel_operation_history_snapshot* snapshot);
    bool remedy_test_channel_get_operation_history(remedy_channel_token_t token, uint32_t operation_kind, uint64_t instance_id, remedy_test_channel_operation_history* out_history);

    bool remedy_test_channel_set_operation_call_cookie(uint64_t cookie);
    bool remedy_test_channel_take_operation_call_cookie(uint64_t* out_cookie);
    bool remedy_test_channel_set_destroy_call_cookie(uint64_t cookie);
    bool remedy_test_channel_take_destroy_call_cookie(uint64_t* out_cookie);

    bool remedy_test_channel_clear_last_created_operation_instance(void);
    bool remedy_test_channel_publish_last_created_operation_instance(uint64_t instance_id);
    bool remedy_test_channel_take_last_created_operation_instance(uint64_t* out_instance_id);

    bool remedy_test_channel_wait_operation_created(remedy_channel_token_t token, uint32_t operation_kind, uint64_t call_cookie, uint32_t timeout_ms, uint64_t* out_instance_id);
    bool remedy_test_channel_wait_operation_pending(remedy_channel_token_t token, uint32_t operation_kind, uint64_t instance_id, uint32_t timeout_ms);

    bool remedy_test_channel_arm_lease_pause(remedy_channel_token_t token);
    bool remedy_test_channel_wait_lease_paused(remedy_channel_token_t token, uint32_t timeout_ms);
    bool remedy_test_channel_release_lease_pause(void);
    bool remedy_test_channel_check_lease_pause_locked(remedy_channel_token_t token);

    bool remedy_test_channel_arm_submission_pause(remedy_channel_token_t token, uint32_t operation_kind, uint64_t call_cookie);
    bool remedy_test_channel_wait_submission_paused(remedy_channel_token_t token, uint32_t operation_kind, uint64_t call_cookie, uint32_t timeout_ms, uint64_t* out_instance_id);
    bool remedy_test_channel_release_submission_pause(void);
    bool remedy_test_channel_check_submission_pause_unlocked(remedy_channel_token_t token, uint32_t operation_kind, uint64_t call_cookie, uint64_t instance_id);

    bool remedy_test_channel_arm_finalizer_pause(remedy_channel_token_t token);
    bool remedy_test_channel_wait_finalizer_paused(remedy_channel_token_t token, uint32_t timeout_ms);
    bool remedy_test_channel_release_finalizer_pause(void);
    bool remedy_test_channel_check_finalizer_pause(remedy_channel_token_t token);
    bool remedy_test_channel_wait_finalizer_draining(remedy_channel_token_t token, uint32_t timeout_ms);

    bool remedy_test_channel_arm_close_pause(remedy_channel_token_t token);
    bool remedy_test_channel_wait_close_paused(remedy_channel_token_t token, uint32_t timeout_ms);
    bool remedy_test_channel_release_close_pause(void);
    bool remedy_test_channel_check_close_pause_unlocked(remedy_channel_token_t token);

    bool remedy_test_channel_inject_pipe_creation_failure(DWORD sim_error);
    bool remedy_test_channel_check_pipe_creation_injection(DWORD* out_sim_error);

    bool remedy_test_channel_inject_pipe_close_failure(remedy_channel_token_t token);
    bool remedy_test_channel_check_pipe_close_injection(remedy_channel_token_t token);
    bool remedy_test_channel_inject_event_close_failure(remedy_channel_token_t token, uint32_t operation_kind);
    bool remedy_test_channel_check_event_close_injection(remedy_channel_token_t token, uint32_t operation_kind);
    bool remedy_test_channel_inject_pipe_cancel_failure(remedy_channel_token_t token, DWORD simulated_error);
    bool remedy_test_channel_consume_pipe_cancel_injection(remedy_channel_token_t token, DWORD* out_simulated_error);
    bool remedy_test_channel_inject_operation_cancel_failure(remedy_channel_token_t token, uint32_t operation_kind, DWORD simulated_error);
    bool remedy_test_channel_consume_operation_cancel_injection(remedy_channel_token_t token, uint32_t operation_kind, DWORD* out_simulated_error);
    bool remedy_test_channel_inject_wait_failure(remedy_channel_token_t token, uint32_t operation_kind, DWORD simulated_error);
    bool remedy_test_channel_consume_wait_failure_injection(remedy_channel_token_t token, uint32_t operation_kind, DWORD* out_simulated_error);

    bool remedy_test_channel_publish_operation_created(remedy_channel_token_t token, uint32_t operation_kind, uint64_t call_cookie, uint64_t instance_id);
    bool remedy_test_channel_publish_operation_pending(remedy_channel_token_t token, uint32_t operation_kind, uint64_t instance_id, bool is_pending);
    bool remedy_test_channel_publish_finalizer_draining(remedy_channel_token_t token, bool is_draining);
    bool remedy_test_channel_publish_finalizer_owner(remedy_channel_token_t token, uint64_t cookie, uint64_t generation_number, bool active, bool draining, bool completed, remedy_err_t result, bool entry_remains_registered);
    bool remedy_test_channel_get_finalizer_owner_observation(remedy_channel_token_t token, channel_finalizer_owner_observation* out_obs);
    bool remedy_test_channel_publish_competitor_attached(remedy_channel_token_t token, uint64_t generation_number, uint64_t competitor_cookie);
    bool remedy_test_channel_wait_competitor_attached(remedy_channel_token_t token, uint64_t generation_number, uint32_t timeout_ms, uint64_t* out_competitor_cookie);

    bool remedy_test_channel_get_entry_snapshot(remedy_channel_token_t token, int* out_state, uint32_t* out_leases, uint32_t* out_close_pins, bool* out_finalizer_active, bool* out_in_registry, bool* out_is_pipe_null, uint32_t* out_retained_event_count);
    bool remedy_test_channel_get_operation_snapshot(remedy_channel_token_t token, uint32_t operation_kind, uint64_t instance_id, int* out_state, bool* out_io_submitted, bool* out_io_pending, bool* out_completion_observed, bool* out_detached, bool* out_event_owned, size_t* out_owned_buffer_size);
    bool remedy_test_channel_get_final_counts(uint32_t* out_active_registry_entries, uint32_t* out_active_public_leases, uint32_t* out_active_close_pins, uint32_t* out_retained_operation_records, uint32_t* out_submitted_pending_operations, uint32_t* out_detached_pending_operations, uint32_t* out_completed_retained_operations, uint32_t* out_operation_buffers_allocated, uint32_t* out_operation_buffers_freed, uint32_t* out_detached_operations_created, uint32_t* out_detached_operations_recovered, uint32_t* out_unconsumed_injections, uint32_t* out_armed_pause_count, uint32_t* out_pending_operation_count, uint32_t* out_finalizer_invocations, uint32_t* out_finalizer_completions, uint32_t* out_successful_finalizers, uint32_t* out_entry_deletions, uint32_t* out_pipe_closures, uint32_t* out_event_closures, uint32_t* out_pipe_close_failures_consumed, uint32_t* out_event_close_failures_consumed, uint32_t* out_pipe_cancel_failures_consumed, uint32_t* out_operation_cancel_failures_consumed, uint32_t* out_wait_failures_consumed);

    bool remedy_test_channel_set_next_token(remedy_channel_token_t next_token, remedy_channel_token_t* out_previous);

    void remedy_test_channel_increment_buffer_allocated(void);
    void remedy_test_channel_increment_buffer_freed(void);
    void remedy_test_channel_increment_detached_created(void);
    void remedy_test_channel_increment_detached_recovered(void);
    void remedy_test_channel_increment_finalizer_invocations(void);
    void remedy_test_channel_increment_finalizer_completions(void);
    void remedy_test_channel_increment_successful_finalizers(void);
    void remedy_test_channel_increment_entry_deletions(void);
    void remedy_test_channel_increment_pipe_closures(void);
    void remedy_test_channel_increment_event_closures(void);
}
#endif

static std::mutex g_channel_mutex;
static std::unordered_map<remedy_channel_token_t, win32_channel_entry*> g_channel_table;
static std::atomic<uint64_t> g_next_channel_token{1};

[[noreturn]] static void channel_fail_invalid_ownership(
    const char* reason)
{
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
    remedy_test_catastrophic_harness_failure(reason);
#else
    std::abort();
#endif
}

static remedy_err_t channel_acquire_live_lease(
    remedy_channel_token_t token, win32_channel_entry** out_entry, channel_lease_guard* out_lease
) {
    if (token == REMEDY_INVALID_CHANNEL_TOKEN || !out_entry || !out_lease || out_lease->get() != nullptr) {
        return REMEDY_ERR_INVALID_ARGUMENT;
    }
    *out_entry = nullptr; win32_channel_entry* entry = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_channel_mutex);
        auto it = g_channel_table.find(token);
        if (it == g_channel_table.end()) return REMEDY_ERR_INVALID_ARGUMENT;
        entry = it->second;
        std::lock_guard<std::mutex> entry_lock(entry->entry_mutex);
        if (entry->state != channel_entry_state::LIVE) return REMEDY_ERR_REVOKING;
        if (entry->active_leases == UINT32_MAX) return REMEDY_ERR_OUT_OF_MEMORY;
        entry->active_leases++;
        out_lease->adopt_locked(entry);
        *out_entry = entry;
    }
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
    remedy_test_channel_check_lease_pause_locked(token);
#endif
    return REMEDY_OK;
}

extern "C" {

remedy_err_t channel_port_create(const remedy_channel_config_t* config, remedy_channel_token_t* out_token) {
    if (!out_token) return REMEDY_ERR_INVALID_ARGUMENT;
    *out_token = REMEDY_INVALID_CHANNEL_TOKEN;
    if (!config || !config->channel_name || config->channel_name[0] == '\0') return REMEDY_ERR_INVALID_ARGUMENT;

    char pipe_path[256];
    int path_len = snprintf(pipe_path, sizeof(pipe_path), "\\\\.\\pipe\\remedy-worker-%s", config->channel_name);
    if (path_len <= 0 || path_len >= (int)sizeof(pipe_path)) return REMEDY_ERR_INVALID_ARGUMENT;

    wchar_t wPath[256];
    int req_chars = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, pipe_path, -1, wPath, 256);
    if (req_chars <= 0 || req_chars >= 256) return REMEDY_ERR_INVALID_ARGUMENT;

    win32_channel_entry* entry = new (std::nothrow) win32_channel_entry();
    if (!entry) return REMEDY_ERR_OUT_OF_MEMORY;
    entry->is_server = config->is_server;
    entry->state = channel_entry_state::CONSTRUCTING;

    remedy_channel_token_t token = REMEDY_INVALID_CHANNEL_TOKEN;
    {
        std::lock_guard<std::mutex> lock(g_channel_mutex);
        remedy_channel_token_t start_token = g_next_channel_token.load(std::memory_order_relaxed);
        do {
            remedy_channel_token_t candidate = g_next_channel_token.fetch_add(1, std::memory_order_relaxed);
            if (candidate == REMEDY_INVALID_CHANNEL_TOKEN) {
                candidate = g_next_channel_token.fetch_add(1, std::memory_order_relaxed);
            }
            if (g_channel_table.find(candidate) == g_channel_table.end()) {
                token = candidate; break;
            }
        } while (g_next_channel_token.load(std::memory_order_relaxed) != start_token);

        if (token == REMEDY_INVALID_CHANNEL_TOKEN) { delete entry; return REMEDY_ERR_OUT_OF_MEMORY; }

        decltype(g_channel_table)::iterator map_it;
        try {
            auto [it, inserted] = g_channel_table.emplace(token, entry);
            if (!inserted) { delete entry; return REMEDY_ERR_OUT_OF_MEMORY; }
            map_it = it;
        } catch (...) { delete entry; return REMEDY_ERR_OUT_OF_MEMORY; }

        DWORD sim_create_err = ERROR_SUCCESS;
        bool creation_injected = false;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
        creation_injected = remedy_test_channel_check_pipe_creation_injection(&sim_create_err);
#endif

        HANDLE hPipe = INVALID_HANDLE_VALUE;
        if (!creation_injected) {
            if (config->is_server) {
                hPipe = CreateNamedPipeW(wPath, PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, 1, 65536, 65536, 0, NULL);
            } else {
                hPipe = CreateFileW(wPath, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, NULL);
            }
        } else {
            SetLastError(sim_create_err != ERROR_SUCCESS ? sim_create_err : ERROR_CANNOT_MAKE);
        }

        if (hPipe == INVALID_HANDLE_VALUE) {
            g_channel_table.erase(map_it);
            delete entry;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
            remedy_test_channel_increment_entry_deletions();
#endif
            return REMEDY_ERR_IPC_FAILURE;
        }

        {
            std::lock_guard<std::mutex> elock(entry->entry_mutex);
            entry->pipe_handle = hPipe;
            entry->state = channel_entry_state::LIVE;
        }
    }
    *out_token = token;
    return REMEDY_OK;
}

} // extern "C"

static remedy_err_t channel_operation_setup(
    win32_channel_entry* entry, remedy_channel_token_t token, channel_operation_kind kind, size_t buffer_size, channel_operation_record** out_record
) {
    channel_operation_record* rec = new (std::nothrow) channel_operation_record();
    if (!rec) return REMEDY_ERR_OUT_OF_MEMORY;

    uint64_t consumed_cookie = 0;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
    remedy_test_channel_take_operation_call_cookie(&consumed_cookie);
#endif

    rec->kind = kind;
    rec->state = channel_operation_state::ALLOCATED;
    rec->completion_owner = channel_completion_owner::PUBLIC_CALL;
    rec->instance_id = g_next_operation_instance_id.fetch_add(1, std::memory_order_relaxed);
    rec->call_cookie = consumed_cookie;

    if (buffer_size > 0) {
        rec->owned_buffer = new (std::nothrow) std::byte[buffer_size];
        if (!rec->owned_buffer) { delete rec; return REMEDY_ERR_OUT_OF_MEMORY; }
        rec->owned_buffer_size = buffer_size;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
        remedy_test_channel_increment_buffer_allocated();
#endif
    }

    HANDLE hEv = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (hEv == NULL) {
        if (rec->owned_buffer) { delete[] rec->owned_buffer;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
            remedy_test_channel_increment_buffer_freed();
#endif
        }
        delete rec; return REMEDY_ERR_IPC_FAILURE;
    }
    rec->event_handle = hEv; rec->overlapped.hEvent = hEv;

    {
        std::lock_guard<std::mutex> lock(entry->entry_mutex);

        if (entry->detached_pending_operations == UINT32_MAX ||
            entry->detachment_reservations >= UINT32_MAX - entry->detached_pending_operations) {
            CloseHandle(hEv);
            if (rec->owned_buffer) delete[] rec->owned_buffer;
            delete rec;
            return REMEDY_ERR_OUT_OF_MEMORY;
        }

        entry->detachment_reservations++;
        rec->detachment_reserved = true;

        rec->state = channel_operation_state::READY_TO_SUBMIT;
        rec->next = entry->retained_events;
        entry->retained_events = rec;
    }

#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
    remedy_test_channel_publish_operation_created(token, (uint32_t)kind, consumed_cookie, rec->instance_id);
    remedy_test_channel_publish_last_created_operation_instance(rec->instance_id);
    remedy_test_channel_check_submission_pause_unlocked(token, (uint32_t)kind, consumed_cookie, rec->instance_id);

    channel_operation_history_snapshot setup_snap{};
    setup_snap.token = token; setup_snap.kind = (uint32_t)kind; setup_snap.instance_id = rec->instance_id;
    setup_snap.created = true; setup_snap.public_call_completed = false;
    if (!remedy_test_channel_record_history_snapshot(&setup_snap)) {
        remedy_test_catastrophic_harness_failure("Initial history publication failed during setup");
    }
#endif

    *out_record = rec;
    return REMEDY_OK;
}

static channel_detach_transition_result channel_detach_pending_operation_locked(
    win32_channel_entry* entry, channel_operation_record* record, channel_operation_transaction& transaction
) {
    if (!entry || !record) return channel_detach_transition_result::INVALID_INTERNAL_STATE;

    channel_operation_record* slow = entry->retained_events;
    channel_operation_record* fast = entry->retained_events;
    uint32_t occurrences = 0;
    while (slow != nullptr) {
        if (slow == record) occurrences++;
        slow = slow->next;
        if (fast != nullptr && fast->next != nullptr) {
            fast = fast->next->next;
            if (slow == fast && slow != nullptr) {
                return channel_detach_transition_result::INVALID_INTERNAL_STATE;
            }
        }
    }
    if (occurrences != 1) return channel_detach_transition_result::INVALID_INTERNAL_STATE;

    if (!record->detachment_reserved || entry->detachment_reservations == 0) return channel_detach_transition_result::INVALID_INTERNAL_STATE;
    if (record->completion_owner != channel_completion_owner::PUBLIC_CALL) return channel_detach_transition_result::INVALID_INTERNAL_STATE;
    if (record->state != channel_operation_state::SUBMITTED_PENDING && record->state != channel_operation_state::CANCEL_REQUESTED) return channel_detach_transition_result::INVALID_INTERNAL_STATE;
    if (!record->current_request_submitted || !record->current_request_pending || record->current_request_completion_observed) return channel_detach_transition_result::INVALID_INTERNAL_STATE;
    if (entry->detached_pending_operations == UINT32_MAX) return channel_detach_transition_result::INVALID_INTERNAL_STATE;

    entry->detachment_reservations--;
    record->detachment_reserved = false;
    entry->detached_pending_operations++;
    record->completion_owner = channel_completion_owner::DETACHED_FINALIZER;
    record->state = channel_operation_state::DETACHED_PENDING;
    record->detached_from_public_call = true;
    record->detached_at_public_return = true;
    record->no_kernel_request_outstanding_at_public_return = false;
    transaction.detached = true;
    entry->cv.notify_all();
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
    remedy_test_channel_increment_detached_created();
#endif
    return channel_detach_transition_result::DETACHED;
}

static channel_record_retire_status channel_retire_completed_record_locked(win32_channel_entry* entry, remedy_channel_token_t token, channel_operation_record* record) {
    if (!entry || !record || record->completion_owner != channel_completion_owner::NONE_TERMINAL) {
        return { channel_record_retire_result::CONTAINMENT_FAILURE, ERROR_INVALID_STATE, false };
    }
    channel_operation_record** cursor = &entry->retained_events; bool found = false;
    while (*cursor != nullptr) { if (*cursor == record) { found = true; break; } cursor = &((*cursor)->next); }
    if (!found) return { channel_record_retire_result::CONTAINMENT_FAILURE, ERROR_INVALID_PARAMETER, false };

    if (record->detachment_reserved) {
        if (entry->detachment_reservations == 0) {
            return { channel_record_retire_result::CONTAINMENT_FAILURE, ERROR_INVALID_STATE, false };
        }
        entry->detachment_reservations--;
        record->detachment_reserved = false;
    }

    bool injected = false;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
    injected = remedy_test_channel_check_event_close_injection(token, (uint32_t)record->kind);
#endif
    if (injected) {
        record->state = channel_operation_state::RETIREMENT_FAILED;
        return { channel_record_retire_result::RETAINED_CLOSE_FAILURE, ERROR_INVALID_HANDLE, false };
    }

    BOOL close_ok = CloseHandle(record->event_handle);
    if (!close_ok) {
        DWORD c_err = GetLastError();
        record->state = channel_operation_state::RETIREMENT_FAILED;
        return { channel_record_retire_result::RETAINED_CLOSE_FAILURE, c_err, false };
    }
    record->event_handle = NULL; record->overlapped.hEvent = NULL; record->event_close_verified = true;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
    remedy_test_channel_increment_event_closures();
#endif
    if (record->owned_buffer) {
        delete[] record->owned_buffer; record->owned_buffer = nullptr;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
        remedy_test_channel_increment_buffer_freed();
#endif
    }
    *cursor = record->next; record->state = channel_operation_state::RETIRED; delete record;
    return { channel_record_retire_result::RETIRED, ERROR_SUCCESS, true };
}

static channel_public_return_result channel_operation_epilogue(
    win32_channel_entry* entry, remedy_channel_token_t token, channel_operation_record* record, channel_operation_transaction& transaction
) {
    remedy_err_t final_result = transaction.primary_error;
    channel_public_return_disposition disposition{channel_public_return_disposition::kind::TERMINAL_CURRENT_REQUEST};
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
    channel_operation_history_snapshot snap{}; bool snap_valid = false;
#endif

    if (entry && record) {
        std::lock_guard<std::mutex> lock(entry->entry_mutex);
        if (record->state == channel_operation_state::SUBMITTED_PENDING || record->state == channel_operation_state::CANCEL_REQUESTED) {
            channel_detach_transition_result dres = channel_detach_pending_operation_locked(entry, record, transaction);
            if (dres == channel_detach_transition_result::DETACHED) {
                final_result = REMEDY_ERR_IPC_FAILURE;
                disposition.value = channel_public_return_disposition::kind::DETACHED_CURRENT_REQUEST;
            } else {
                channel_fail_invalid_ownership(
                    "Invalid detachment ownership transition");
            }
        }

        bool proof_1 = (disposition.value == channel_public_return_disposition::kind::TERMINAL_CURRENT_REQUEST || disposition.value == channel_public_return_disposition::kind::NO_CURRENT_REQUEST) &&
                       (!record->current_request_submitted && !record->current_request_pending && record->completion_owner == channel_completion_owner::NONE_TERMINAL);
        bool proof_2 = (disposition.value == channel_public_return_disposition::kind::TERMINAL_CURRENT_REQUEST) &&
                       (record->current_request_submitted && !record->current_request_pending && record->current_request_completion_observed && record->completion_owner == channel_completion_owner::NONE_TERMINAL);
        bool proof_3 = (disposition.value == channel_public_return_disposition::kind::DETACHED_CURRENT_REQUEST) &&
                       (record->state == channel_operation_state::DETACHED_PENDING && record->current_request_submitted && record->current_request_pending && record->completion_owner == channel_completion_owner::DETACHED_FINALIZER);

        if (!(proof_1 || proof_2 || proof_3)) {
            channel_fail_invalid_ownership(
                "Violation of lease release precondition in epilogue");
        }

#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
        snap.token = token; snap.kind = (uint32_t)record->kind; snap.instance_id = record->instance_id; snap.created = true;
        snap.io_submitted = record->ever_submitted; snap.io_was_pending = record->ever_pending;
        snap.kernel_completion_observed = record->ever_completion_observed;
        snap.detached_at_public_return = record->detached_at_public_return; snap.detached_recovered = record->detached_recovered;
        snap.no_kernel_request_outstanding_at_public_return = record->no_kernel_request_outstanding_at_public_return;
        snap.final_completion_error = (transaction.wait_error != ERROR_SUCCESS) ? transaction.wait_error : record->completion_error; snap_valid = true;
#endif

        if (disposition.value == channel_public_return_disposition::kind::TERMINAL_CURRENT_REQUEST && record->state != channel_operation_state::DETACHED_PENDING) {
            if (entry->finalizer_active) {
                record->state = channel_operation_state::COMPLETED;
            } else {
                channel_record_retire_status rstat = channel_retire_completed_record_locked(entry, token, record);
                if (rstat.result == channel_record_retire_result::CONTAINMENT_FAILURE) {
                    final_result = REMEDY_ERR_CONTAINMENT_FAILED;
                } else if (rstat.result == channel_record_retire_result::RETAINED_CLOSE_FAILURE) {
                    final_result = REMEDY_ERR_IPC_FAILURE;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
                    snap.final_completion_error = rstat.win32_error;
#endif
                }
            }
        }
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
        snap.final_public_result = final_result; snap.public_call_completed = true;
#endif
    }

#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
    if (snap_valid) remedy_test_channel_record_history_snapshot(&snap);
#endif
    return { disposition, final_result };
}

static channel_cancel_result channel_cancel_io_checked(remedy_channel_token_t token, HANDLE pipe_handle, OVERLAPPED* overlapped, bool is_close, DWORD sim_err) {
    if (pipe_handle == INVALID_HANDLE_VALUE || pipe_handle == NULL) return { false, ERROR_INVALID_HANDLE, false };
    if (sim_err != ERROR_SUCCESS) {
        return { false, sim_err, (overlapped != nullptr) };
    }
    BOOL cancel_ok = CancelIoEx(pipe_handle, overlapped);
    DWORD err = cancel_ok ? ERROR_SUCCESS : GetLastError();
    if (!cancel_ok && (err == ERROR_NOT_FOUND || err == ERROR_INVALID_HANDLE)) return { true, ERROR_SUCCESS, false };
    return { cancel_ok != FALSE, err, cancel_ok != FALSE };
}

static channel_wait_result channel_wait_once_classified(
    remedy_channel_token_t token,
    channel_operation_kind operation_kind,
    HANDLE event_handle,
    DWORD wait_ms
) {
    if (event_handle == NULL || event_handle == INVALID_HANDLE_VALUE) {
        return { channel_wait_reason::WAIT_REASON_FAILED, ERROR_INVALID_HANDLE, 0 };
    }

    DWORD sim_err = ERROR_SUCCESS;
    bool injected = false;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
    injected = remedy_test_channel_consume_wait_failure_injection(token, (uint32_t)operation_kind, &sim_err);
#endif
    if (injected) {
        return { channel_wait_reason::WAIT_REASON_FAILED, (sim_err != ERROR_SUCCESS) ? sim_err : ERROR_UNHANDLED_EXCEPTION, 0 };
    }

    DWORD wait_res = WaitForSingleObject(event_handle, wait_ms);
    if (wait_res == WAIT_OBJECT_0) {
        return { channel_wait_reason::WAIT_REASON_COMPLETED, ERROR_SUCCESS, 0 };
    } else if (wait_res == WAIT_TIMEOUT) {
        return { channel_wait_reason::WAIT_REASON_TIMED_OUT, ERROR_TIMEOUT, 0 };
    } else if (wait_res == WAIT_FAILED) {
        DWORD err = GetLastError();
        return { channel_wait_reason::WAIT_REASON_FAILED, err, 0 };
    } else {
        return { channel_wait_reason::WAIT_REASON_FAILED, ERROR_INVALID_STATE, 0 };
    }
}

static channel_submit_result channel_submit_chunk_locked(
    win32_channel_entry* entry, channel_operation_record* record, size_t owned_buffer_offset, DWORD byte_count, channel_submit_kind submit_kind
) {
    if (entry->state != channel_entry_state::LIVE) {
        record->completion_owner = channel_completion_owner::NONE_TERMINAL;
        record->current_request_submitted = false; record->current_request_pending = false; record->current_request_completion_observed = false;
        record->no_kernel_request_outstanding_at_public_return = true; record->state = channel_operation_state::READY_TO_SUBMIT;
        return { channel_submit_reason::ADMISSION_REJECTED, ERROR_CANCELLED, 0, channel_operation_state::READY_TO_SUBMIT };
    }

    std::byte* submit_buffer = nullptr;
    if (submit_kind != channel_submit_kind::CONNECT) {
        if (!record->owned_buffer || owned_buffer_offset > record->owned_buffer_size || byte_count > (record->owned_buffer_size - owned_buffer_offset)) {
            record->completion_owner = channel_completion_owner::NONE_TERMINAL; record->no_kernel_request_outstanding_at_public_return = true;
            return { channel_submit_reason::IMMEDIATE_FAILURE, ERROR_INVALID_PARAMETER, 0, channel_operation_state::SUBMITTED_FAILED_IMMEDIATE };
        }
        submit_buffer = record->owned_buffer + owned_buffer_offset;
        record->submitted_buffer_offset = owned_buffer_offset; record->submitted_chunk_length = byte_count;
    }

    HANDLE hEv = record->event_handle; ZeroMemory(&record->overlapped, sizeof(record->overlapped)); record->overlapped.hEvent = hEv;
    record->current_request_submitted = false; record->current_request_pending = false; record->current_request_completion_observed = false;
    record->submission_error = ERROR_SUCCESS; record->cancellation_error = ERROR_SUCCESS; record->completion_error = ERROR_SUCCESS;
    record->requested_size = byte_count; record->transferred_size = 0; record->state = channel_operation_state::READY_TO_SUBMIT;

    if (!ResetEvent(record->event_handle)) {
        DWORD r_err = GetLastError(); record->submission_error = r_err; record->completion_owner = channel_completion_owner::NONE_TERMINAL;
        record->no_kernel_request_outstanding_at_public_return = true;
        return { channel_submit_reason::RESET_FAILED, r_err, 0, channel_operation_state::READY_TO_SUBMIT };
    }
    record->ever_submitted = true; BOOL submit_ok = FALSE; DWORD submit_err = ERROR_SUCCESS; DWORD dummy_transferred = 0;

    if (submit_kind == channel_submit_kind::CONNECT) {
        submit_ok = ConnectNamedPipe(entry->pipe_handle, &record->overlapped);
        if (!submit_ok) submit_err = GetLastError();
        if (!submit_ok && submit_err == ERROR_PIPE_CONNECTED) { submit_ok = TRUE; submit_err = ERROR_SUCCESS; }
    } else if (submit_kind == channel_submit_kind::READ) {
        submit_ok = ReadFile(entry->pipe_handle, submit_buffer, byte_count, &dummy_transferred, &record->overlapped);
        if (!submit_ok) submit_err = GetLastError();
    } else if (submit_kind == channel_submit_kind::WRITE) {
        submit_ok = WriteFile(entry->pipe_handle, submit_buffer, byte_count, &dummy_transferred, &record->overlapped);
        if (!submit_ok) submit_err = GetLastError();
    }

    record->current_request_submitted = true;
    if (submit_ok) {
        if (submit_kind != channel_submit_kind::CONNECT && dummy_transferred > byte_count) {
            record->current_request_pending = false; record->current_request_completion_observed = true; record->ever_completion_observed = true;
            record->no_kernel_request_outstanding_at_public_return = true; record->submission_error = ERROR_BAD_LENGTH; record->completion_owner = channel_completion_owner::NONE_TERMINAL;
            record->state = channel_operation_state::SUBMITTED_FAILED_IMMEDIATE;
            return { channel_submit_reason::IMMEDIATE_FAILURE, ERROR_BAD_LENGTH, 0, channel_operation_state::SUBMITTED_FAILED_IMMEDIATE };
        }
        if (submit_kind != channel_submit_kind::CONNECT && byte_count > 0 && dummy_transferred == 0) {
            DWORD fault_err = (submit_kind == channel_submit_kind::READ) ? ERROR_READ_FAULT : ERROR_WRITE_FAULT;
            record->current_request_pending = false; record->current_request_completion_observed = true; record->ever_completion_observed = true;
            record->no_kernel_request_outstanding_at_public_return = true; record->submission_error = fault_err; record->completion_owner = channel_completion_owner::NONE_TERMINAL;
            record->state = channel_operation_state::SUBMITTED_FAILED_IMMEDIATE;
            return { channel_submit_reason::IMMEDIATE_FAILURE, fault_err, 0, channel_operation_state::SUBMITTED_FAILED_IMMEDIATE };
        }
        record->current_request_pending = false; record->current_request_completion_observed = true; record->ever_completion_observed = true;
        record->no_kernel_request_outstanding_at_public_return = true; record->transferred_size = dummy_transferred; record->state = channel_operation_state::CHUNK_COMPLETED;
        return { channel_submit_reason::IMMEDIATE_SUCCESS, ERROR_SUCCESS, dummy_transferred, channel_operation_state::CHUNK_COMPLETED };
    } else if (submit_err == ERROR_IO_PENDING) {
        record->current_request_pending = true; record->current_request_completion_observed = false; record->ever_pending = true;
        record->no_kernel_request_outstanding_at_public_return = false; record->state = channel_operation_state::SUBMITTED_PENDING;
        return { channel_submit_reason::PENDING, ERROR_IO_PENDING, 0, channel_operation_state::SUBMITTED_PENDING };
    } else {
        record->current_request_pending = false; record->current_request_completion_observed = true; record->ever_completion_observed = true;
        record->no_kernel_request_outstanding_at_public_return = true; record->submission_error = submit_err; record->completion_owner = channel_completion_owner::NONE_TERMINAL;
        record->state = channel_operation_state::SUBMITTED_FAILED_IMMEDIATE;
        return { channel_submit_reason::IMMEDIATE_FAILURE, submit_err, 0, channel_operation_state::SUBMITTED_FAILED_IMMEDIATE };
    }
}

static channel_transfer_result channel_transfer_exact(
    win32_channel_entry* entry,
    remedy_channel_token_t token,
    channel_operation_record* record,
    size_t buffer_offset,
    size_t exact_length,
    channel_submit_kind kind,
    uint32_t timeout_ms,
    channel_operation_transaction& transaction
) {
    if (!entry || !record || (kind != channel_submit_kind::READ && kind != channel_submit_kind::WRITE)) {
        return { channel_transfer_status::IPC_FAILURE, ERROR_INVALID_PARAMETER, 0 };
    }
    if (buffer_offset > record->owned_buffer_size ||
        exact_length > (record->owned_buffer_size - buffer_offset) ||
        exact_length > UINT32_MAX) {
        return { channel_transfer_status::BAD_LENGTH, ERROR_BAD_LENGTH, 0 };
    }

    {
        std::lock_guard<std::mutex> lock(entry->entry_mutex);
        record->cumulative_target = exact_length;
        record->cumulative_transferred = 0;
    }

    if (exact_length == 0) {
        return { channel_transfer_status::COMPLETED, ERROR_SUCCESS, 0 };
    }

    size_t completed = 0;
    ULONGLONG phase_start = GetTickCount64();

    while (completed < exact_length) {
        ULONGLONG elapsed = GetTickCount64() - phase_start;
        if (elapsed >= timeout_ms) {
            std::lock_guard<std::mutex> lock(entry->entry_mutex);
            record->current_request_pending = false;
            record->no_kernel_request_outstanding_at_public_return = true;
            record->completion_owner = channel_completion_owner::NONE_TERMINAL;
            record->completion_error = ERROR_TIMEOUT;
            if (record->ever_submitted) {
                record->current_request_submitted = true;
                record->current_request_completion_observed = true;
                record->state = channel_operation_state::COMPLETED;
            } else {
                record->current_request_submitted = false;
                record->current_request_completion_observed = false;
                record->state = channel_operation_state::READY_TO_SUBMIT;
            }
            return { channel_transfer_status::IPC_FAILURE, ERROR_TIMEOUT, completed };
        }

        size_t next_offset = buffer_offset + completed;
        size_t remaining_size = exact_length - completed;
        DWORD remaining = static_cast<DWORD>(remaining_size);

        if (completed > exact_length || next_offset > record->owned_buffer_size || remaining_size > (record->owned_buffer_size - next_offset)) {
            return { channel_transfer_status::BAD_LENGTH, ERROR_BAD_LENGTH, completed };
        }

        channel_submit_result sres{};
        {
            std::lock_guard<std::mutex> lock(entry->entry_mutex);

            bool no_prev = (!record->current_request_submitted && !record->current_request_pending && record->no_kernel_request_outstanding_at_public_return);
            bool prev_term = (record->current_request_submitted && !record->current_request_pending && record->current_request_completion_observed && record->no_kernel_request_outstanding_at_public_return);
            if (!no_prev && !prev_term) {
                return { channel_transfer_status::IPC_FAILURE, ERROR_INVALID_STATE, completed };
            }

            sres = channel_submit_chunk_locked(entry, record, next_offset, remaining, kind);
            if (sres.reason == channel_submit_reason::IMMEDIATE_SUCCESS) {
                if (sres.transferred_bytes > remaining) {
                    record->completion_owner = channel_completion_owner::NONE_TERMINAL;
                    record->state = channel_operation_state::SUBMITTED_FAILED_IMMEDIATE;
                    record->completion_error = ERROR_BAD_LENGTH;
                    return { channel_transfer_status::BAD_LENGTH, ERROR_BAD_LENGTH, completed };
                }
                if (sres.transferred_bytes == 0) {
                    DWORD fault_err = (kind == channel_submit_kind::READ) ? ERROR_READ_FAULT : ERROR_WRITE_FAULT;
                    record->completion_owner = channel_completion_owner::NONE_TERMINAL;
                    record->state = channel_operation_state::SUBMITTED_FAILED_IMMEDIATE;
                    record->completion_error = fault_err;
                    return { channel_transfer_status::IPC_FAILURE, fault_err, completed };
                }
                completed += sres.transferred_bytes;
                record->cumulative_transferred = completed;
                if (completed == exact_length) {
                    return { channel_transfer_status::COMPLETED, ERROR_SUCCESS, completed };
                }
                continue;
            } else if (sres.reason == channel_submit_reason::PENDING) {
                transaction.pending_published = true;
            } else {
                record->completion_owner = channel_completion_owner::NONE_TERMINAL;
                record->completion_error = sres.win32_error;
                return { channel_transfer_status::IPC_FAILURE, sres.win32_error, completed };
            }
        }

        if (transaction.pending_published) {
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
            remedy_test_channel_publish_operation_pending(token, (uint32_t)record->kind, record->instance_id, true);
#endif
            bool chunk_completed = false;
            DWORD wait_err = ERROR_SUCCESS;

            for (;;) {
                ULONGLONG elapsed = GetTickCount64() - phase_start;
                if (elapsed >= timeout_ms) {
                    wait_err = ERROR_TIMEOUT;
                    break;
                }
                DWORD remaining_ms = static_cast<DWORD>(timeout_ms - elapsed);
                DWORD wait_slice = (remaining_ms < 50) ? remaining_ms : 50;

                channel_wait_result wres = channel_wait_once_classified(token, record->kind, record->event_handle, wait_slice);
                if (wres.reason == channel_wait_reason::WAIT_REASON_COMPLETED) {
                    chunk_completed = true;
                    break;
                } else if (wres.reason == channel_wait_reason::WAIT_REASON_TIMED_OUT) {
                    std::lock_guard<std::mutex> lock(entry->entry_mutex);
                    if (entry->state != channel_entry_state::LIVE) {
                        wait_err = ERROR_CANCELLED;
                        break;
                    }
                } else {
                    wait_err = wres.win32_error;
                    break;
                }
            }

            if (!chunk_completed) {
                std::lock_guard<std::mutex> lock(entry->entry_mutex);
                DWORD sim_canc_err = ERROR_SUCCESS;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
                remedy_test_channel_consume_operation_cancel_injection(token, (uint32_t)record->kind, &sim_canc_err);
#endif
                channel_cancel_result cres = channel_cancel_io_checked(token, entry->pipe_handle, &record->overlapped, false, sim_canc_err);
                record->state = channel_operation_state::CANCEL_REQUESTED;
                DWORD final_err = (wait_err != ERROR_SUCCESS) ? wait_err : ERROR_TIMEOUT;
                record->cancellation_error = (!cres.success) ? cres.win32_error : final_err;
                transaction.wait_error = final_err;
                return { channel_transfer_status::PENDING_UNRESOLVED, final_err, completed };
            }

            std::lock_guard<std::mutex> lock(entry->entry_mutex);
            DWORD transferred = 0;
            BOOL get_ok = GetOverlappedResult(entry->pipe_handle, &record->overlapped, &transferred, FALSE);
            DWORD comp_err = get_ok ? ERROR_SUCCESS : GetLastError();

            if (get_ok) {
                if (transferred > remaining) {
                    record->current_request_pending = false; record->current_request_completion_observed = true;
                    record->ever_completion_observed = true; record->no_kernel_request_outstanding_at_public_return = true;
                    record->completion_owner = channel_completion_owner::NONE_TERMINAL; record->state = channel_operation_state::COMPLETED;
                    record->completion_error = ERROR_BAD_LENGTH; record->transferred_size = transferred;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
                    remedy_test_channel_publish_operation_pending(token, (uint32_t)record->kind, record->instance_id, false);
#endif
                    transaction.pending_published = false;
                    return { channel_transfer_status::BAD_LENGTH, ERROR_BAD_LENGTH, completed };
                }
                if (transferred == 0) {
                    DWORD fault_code = (kind == channel_submit_kind::READ) ? ERROR_READ_FAULT : ERROR_WRITE_FAULT;
                    record->current_request_pending = false; record->current_request_completion_observed = true;
                    record->ever_completion_observed = true; record->no_kernel_request_outstanding_at_public_return = true;
                    record->completion_owner = channel_completion_owner::NONE_TERMINAL; record->state = channel_operation_state::COMPLETED;
                    record->completion_error = fault_code; record->transferred_size = 0;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
                    remedy_test_channel_publish_operation_pending(token, (uint32_t)record->kind, record->instance_id, false);
#endif
                    transaction.pending_published = false;
                    return { channel_transfer_status::IPC_FAILURE, fault_code, completed };
                }
                completed += transferred;
                record->cumulative_transferred = completed;
                record->current_request_submitted = true;
                record->current_request_pending = false;
                record->current_request_completion_observed = true;
                record->ever_completion_observed = true;
                record->no_kernel_request_outstanding_at_public_return = true;
                record->state = channel_operation_state::CHUNK_COMPLETED;
                record->completion_owner = channel_completion_owner::PUBLIC_CALL;
                record->completion_error = ERROR_SUCCESS;
                record->transferred_size = transferred;

#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
                remedy_test_channel_publish_operation_pending(token, (uint32_t)record->kind, record->instance_id, false);
#endif
                transaction.pending_published = false;
                if (completed == exact_length) {
                    return { channel_transfer_status::COMPLETED, ERROR_SUCCESS, completed };
                }
                continue;
            } else if (comp_err == ERROR_IO_INCOMPLETE) {
                record->current_request_submitted = true; record->current_request_pending = true; record->current_request_completion_observed = false;
                record->no_kernel_request_outstanding_at_public_return = false; record->completion_owner = channel_completion_owner::PUBLIC_CALL;
                record->state = channel_operation_state::SUBMITTED_PENDING;
                return { channel_transfer_status::PENDING_UNRESOLVED, ERROR_IO_INCOMPLETE, completed };
            } else {
                DWORD sim_canc_err = ERROR_SUCCESS;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
                remedy_test_channel_consume_operation_cancel_injection(token, (uint32_t)record->kind, &sim_canc_err);
#endif
                if (sim_canc_err != ERROR_SUCCESS) {
                    record->cancellation_failed = true;
                    record->cancellation_error = sim_canc_err;
                    record->current_request_submitted = true;
                    record->current_request_pending = true;
                    record->current_request_completion_observed = false;
                    record->no_kernel_request_outstanding_at_public_return = false;
                    record->completion_owner = channel_completion_owner::PUBLIC_CALL;
                    record->state = channel_operation_state::CANCEL_REQUESTED;
                    transaction.wait_error = sim_canc_err;
                    return { channel_transfer_status::PENDING_UNRESOLVED, sim_canc_err, completed };
                }
                record->current_request_submitted = true; record->current_request_pending = false; record->current_request_completion_observed = true;
                record->ever_completion_observed = true; record->no_kernel_request_outstanding_at_public_return = true;
                record->completion_owner = channel_completion_owner::NONE_TERMINAL; record->state = channel_operation_state::COMPLETED;
                record->completion_error = comp_err; record->transferred_size = transferred;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
                remedy_test_channel_publish_operation_pending(token, (uint32_t)record->kind, record->instance_id, false);
#endif
                transaction.pending_published = false;
                return { channel_transfer_status::IPC_FAILURE, comp_err, completed };
            }
        }
    }
    return { channel_transfer_status::COMPLETED, ERROR_SUCCESS, completed };
}

extern "C" {

remedy_err_t channel_port_connect(remedy_channel_token_t token, uint32_t timeout_ms) {
    win32_channel_entry* entry = nullptr; channel_lease_guard lease;
    remedy_err_t l_err = channel_acquire_live_lease(token, &entry, &lease);
    if (l_err != REMEDY_OK) return l_err;

    channel_operation_record* rec = nullptr;
    remedy_err_t s_err = channel_operation_setup(entry, token, channel_operation_kind::CONNECT, 0, &rec);
    if (s_err != REMEDY_OK) return s_err;

    channel_operation_transaction tx{};
    {
        std::lock_guard<std::mutex> lock(entry->entry_mutex);
        channel_submit_result sres = channel_submit_chunk_locked(entry, rec, 0, 0, channel_submit_kind::CONNECT);
        if (sres.reason == channel_submit_reason::IMMEDIATE_SUCCESS) {
            rec->completion_owner = channel_completion_owner::NONE_TERMINAL; rec->state = channel_operation_state::COMPLETED; tx.primary_error = REMEDY_OK;
        } else if (sres.reason == channel_submit_reason::PENDING) {
            tx.pending_published = true;
        } else {
            rec->completion_owner = channel_completion_owner::NONE_TERMINAL; tx.primary_error = REMEDY_ERR_IPC_FAILURE;
        }
    }

    if (tx.pending_published) {
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
        remedy_test_channel_publish_operation_pending(token, (uint32_t)rec->kind, rec->instance_id, true);
#endif
        ULONGLONG phase_start = GetTickCount64();
        bool completed = false;
        DWORD wait_err = ERROR_SUCCESS;

        for (;;) {
            ULONGLONG elapsed = GetTickCount64() - phase_start;
            if (elapsed >= timeout_ms) {
                wait_err = ERROR_TIMEOUT;
                break;
            }
            DWORD remaining_ms = static_cast<DWORD>(timeout_ms - elapsed);
            DWORD wait_slice = (remaining_ms < 50) ? remaining_ms : 50;

            channel_wait_result wres = channel_wait_once_classified(token, rec->kind, rec->event_handle, wait_slice);
            if (wres.reason == channel_wait_reason::WAIT_REASON_COMPLETED) {
                completed = true;
                break;
            } else if (wres.reason == channel_wait_reason::WAIT_REASON_TIMED_OUT) {
                std::lock_guard<std::mutex> lock(entry->entry_mutex);
                if (entry->state != channel_entry_state::LIVE) {
                    wait_err = ERROR_CANCELLED;
                    break;
                }
            } else {
                wait_err = wres.win32_error;
                break;
            }
        }

        std::lock_guard<std::mutex> lock(entry->entry_mutex);
        if (completed) {
            DWORD transferred = 0;
            BOOL get_ok = GetOverlappedResult(entry->pipe_handle, &rec->overlapped, &transferred, FALSE);
            DWORD completion_error = get_ok ? ERROR_SUCCESS : GetLastError();
            if (get_ok || completion_error == ERROR_PIPE_CONNECTED) {
                rec->current_request_pending = false; rec->current_request_completion_observed = true;
                rec->ever_completion_observed = true; rec->no_kernel_request_outstanding_at_public_return = true;
                rec->completion_owner = channel_completion_owner::NONE_TERMINAL; rec->state = channel_operation_state::COMPLETED;
                rec->completion_error = ERROR_SUCCESS; rec->transferred_size = transferred; tx.primary_error = REMEDY_OK;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
                remedy_test_channel_publish_operation_pending(token, (uint32_t)rec->kind, rec->instance_id, false);
#endif
                tx.pending_published = false;
            } else if (completion_error == ERROR_IO_INCOMPLETE) {
                rec->current_request_submitted = true; rec->current_request_pending = true; rec->current_request_completion_observed = false;
                rec->no_kernel_request_outstanding_at_public_return = false; rec->completion_owner = channel_completion_owner::PUBLIC_CALL;
                rec->state = channel_operation_state::SUBMITTED_PENDING; tx.primary_error = REMEDY_ERR_IPC_FAILURE;
            } else {
                rec->current_request_submitted = true; rec->current_request_pending = false; rec->current_request_completion_observed = true;
                rec->ever_completion_observed = true; rec->no_kernel_request_outstanding_at_public_return = true;
                rec->completion_owner = channel_completion_owner::NONE_TERMINAL; rec->state = channel_operation_state::COMPLETED;
                rec->completion_error = completion_error; rec->transferred_size = transferred; tx.primary_error = REMEDY_ERR_IPC_FAILURE;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
                remedy_test_channel_publish_operation_pending(token, (uint32_t)rec->kind, rec->instance_id, false);
#endif
                tx.pending_published = false;
            }
        } else {
            DWORD sim_canc_err = ERROR_SUCCESS;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
            remedy_test_channel_consume_operation_cancel_injection(token, (uint32_t)rec->kind, &sim_canc_err);
#endif
            channel_cancel_result cres = channel_cancel_io_checked(token, entry->pipe_handle, &rec->overlapped, false, sim_canc_err);
            rec->current_request_submitted = true;
            rec->current_request_pending = true;
            rec->current_request_completion_observed = false;
            rec->no_kernel_request_outstanding_at_public_return = false;
            rec->completion_owner = channel_completion_owner::PUBLIC_CALL;
            rec->state = channel_operation_state::CANCEL_REQUESTED;

            DWORD final_err = (wait_err != ERROR_SUCCESS) ? wait_err : ERROR_TIMEOUT;
            rec->cancellation_error = (!cres.success) ? cres.win32_error : final_err;
            tx.wait_error = final_err;

            if (wait_err == ERROR_TIMEOUT) {
                tx.primary_error = REMEDY_ERR_TIMEOUT;
            } else {
                tx.primary_error = REMEDY_ERR_IPC_FAILURE;
            }
        }
    }

    channel_public_return_result ret_res = channel_operation_epilogue(entry, token, rec, tx);
    switch (ret_res.disposition.value) {
        case channel_public_return_disposition::kind::NO_CURRENT_REQUEST:
        case channel_public_return_disposition::kind::TERMINAL_CURRENT_REQUEST:
        case channel_public_return_disposition::kind::DETACHED_CURRENT_REQUEST:
            lease.release();
            break;
        case channel_public_return_disposition::kind::INVALID_OWNERSHIP:
        default:
            channel_fail_invalid_ownership(
                "Unexpected INVALID_OWNERSHIP public disposition");
    }
    return ret_res.api_result;
}

remedy_err_t channel_port_send_frame(remedy_channel_token_t token, const remedy_wire_frame_header_t* header, const void* payload) {
    if (!header || (header->payload_len > 0 && !payload)) return REMEDY_ERR_INVALID_ARGUMENT;
    if (header->payload_len > (UINT32_MAX - REMEDY_WIRE_HEADER_SIZE)) return REMEDY_ERR_INVALID_ARGUMENT;

    win32_channel_entry* entry = nullptr; channel_lease_guard lease;
    remedy_err_t l_err = channel_acquire_live_lease(token, &entry, &lease);
    if (l_err != REMEDY_OK) return l_err;

    remedy_wire_frame_header_t local_header = *header;
    local_header.magic = REMEDY_WIRE_MAGIC;
    local_header.version = REMEDY_WIRE_VERSION;
    local_header.header_len = REMEDY_WIRE_HEADER_SIZE;

    local_header.checksum =
        local_header.payload_len > 0
            ? remedy_adler32(
                  static_cast<const uint8_t*>(payload),
                  local_header.payload_len)
            : 0;

    size_t total_len = REMEDY_WIRE_HEADER_SIZE + local_header.payload_len;
    channel_operation_record* rec = nullptr;
    remedy_err_t s_err = channel_operation_setup(entry, token, channel_operation_kind::WRITE, total_len, &rec);
    if (s_err != REMEDY_OK) return s_err;

    remedy_wire_frame_encode(
        &local_header,
        reinterpret_cast<uint8_t*>(rec->owned_buffer));
    if (local_header.payload_len > 0) std::memcpy(rec->owned_buffer + REMEDY_WIRE_HEADER_SIZE, payload, local_header.payload_len);

    channel_operation_transaction tx{};
    channel_transfer_result tres = channel_transfer_exact(entry, token, rec, 0, total_len, channel_submit_kind::WRITE, 5000, tx);

    {
        std::lock_guard<std::mutex> lock(entry->entry_mutex);
        if (tres.status == channel_transfer_status::COMPLETED && tres.cumulative_transferred == total_len) {
            rec->completion_owner = channel_completion_owner::NONE_TERMINAL;
            rec->state = channel_operation_state::COMPLETED;
            tx.primary_error = REMEDY_OK;
        } else if (tres.status == channel_transfer_status::PENDING_UNRESOLVED) {
            tx.primary_error = (tres.last_error == ERROR_TIMEOUT) ? REMEDY_ERR_TIMEOUT : REMEDY_ERR_IPC_FAILURE;
        } else {
            rec->completion_owner = channel_completion_owner::NONE_TERMINAL;
            tx.primary_error = (tres.last_error == ERROR_TIMEOUT) ? REMEDY_ERR_TIMEOUT : REMEDY_ERR_IPC_FAILURE;
        }
    }

    channel_public_return_result ret_res = channel_operation_epilogue(entry, token, rec, tx);
    switch (ret_res.disposition.value) {
        case channel_public_return_disposition::kind::NO_CURRENT_REQUEST:
        case channel_public_return_disposition::kind::TERMINAL_CURRENT_REQUEST:
        case channel_public_return_disposition::kind::DETACHED_CURRENT_REQUEST:
            lease.release();
            break;
        case channel_public_return_disposition::kind::INVALID_OWNERSHIP:
        default:
            channel_fail_invalid_ownership(
                "Unexpected INVALID_OWNERSHIP public disposition");
    }
    return ret_res.api_result;
}

remedy_err_t channel_port_read_frame(remedy_channel_token_t token, remedy_wire_frame_header_t* out_header, void* payload_buffer, size_t max_payload_len) {
    if (!out_header) return REMEDY_ERR_INVALID_ARGUMENT;
    if (max_payload_len > (SIZE_MAX - REMEDY_WIRE_HEADER_SIZE)) return REMEDY_ERR_INVALID_ARGUMENT;

    win32_channel_entry* entry = nullptr; channel_lease_guard lease;
    remedy_err_t l_err = channel_acquire_live_lease(token, &entry, &lease);
    if (l_err != REMEDY_OK) return l_err;

    size_t req_len = REMEDY_WIRE_HEADER_SIZE + max_payload_len;
    channel_operation_record* rec = nullptr;
    remedy_err_t s_err = channel_operation_setup(entry, token, channel_operation_kind::READ, req_len, &rec);
    if (s_err != REMEDY_OK) return s_err;

    channel_operation_transaction tx{};

    // Phase 1: Exact Read Header
    channel_transfer_result h_res = channel_transfer_exact(entry, token, rec, 0, REMEDY_WIRE_HEADER_SIZE, channel_submit_kind::READ, 5000, tx);

    if (h_res.status != channel_transfer_status::COMPLETED || h_res.cumulative_transferred != REMEDY_WIRE_HEADER_SIZE) {
        {
            std::lock_guard<std::mutex> lock(entry->entry_mutex);
            if (h_res.status == channel_transfer_status::PENDING_UNRESOLVED) {
                tx.primary_error = (h_res.last_error == ERROR_TIMEOUT) ? REMEDY_ERR_TIMEOUT : REMEDY_ERR_IPC_FAILURE;
            } else {
                rec->completion_owner = channel_completion_owner::NONE_TERMINAL;
                tx.primary_error = (h_res.last_error == ERROR_TIMEOUT) ? REMEDY_ERR_TIMEOUT : REMEDY_ERR_IPC_FAILURE;
            }
        }
        channel_public_return_result ret_res = channel_operation_epilogue(entry, token, rec, tx);
        switch (ret_res.disposition.value) {
            case channel_public_return_disposition::kind::NO_CURRENT_REQUEST:
            case channel_public_return_disposition::kind::TERMINAL_CURRENT_REQUEST:
            case channel_public_return_disposition::kind::DETACHED_CURRENT_REQUEST:
                lease.release();
                break;
            case channel_public_return_disposition::kind::INVALID_OWNERSHIP:
            default:
                channel_fail_invalid_ownership(
                    "Unexpected INVALID_OWNERSHIP public disposition");
        }
        return ret_res.api_result;
    }

    remedy_wire_frame_header_t header_tmp{};
    remedy_err_t dec_err = REMEDY_OK;
    {
        std::lock_guard<std::mutex> lock(entry->entry_mutex);
        dec_err = remedy_wire_frame_decode(reinterpret_cast<const uint8_t*>(rec->owned_buffer), &header_tmp);
    }

    if (dec_err != REMEDY_OK || header_tmp.payload_len > max_payload_len || (header_tmp.payload_len > 0 && !payload_buffer) ||
        header_tmp.payload_len > (rec->owned_buffer_size - REMEDY_WIRE_HEADER_SIZE)) {
        {
            std::lock_guard<std::mutex> lock(entry->entry_mutex);
            rec->completion_owner = channel_completion_owner::NONE_TERMINAL;
            rec->state = channel_operation_state::COMPLETED;
            rec->completion_error = (dec_err != REMEDY_OK) ? ERROR_INVALID_DATA : ERROR_BAD_LENGTH;
            tx.primary_error = (dec_err != REMEDY_OK) ? dec_err : REMEDY_ERR_INVALID_ARGUMENT;
        }

        channel_public_return_result ret_res = channel_operation_epilogue(entry, token, rec, tx);
        switch (ret_res.disposition.value) {
            case channel_public_return_disposition::kind::NO_CURRENT_REQUEST:
            case channel_public_return_disposition::kind::TERMINAL_CURRENT_REQUEST:
            case channel_public_return_disposition::kind::DETACHED_CURRENT_REQUEST:
                lease.release();
                break;
            case channel_public_return_disposition::kind::INVALID_OWNERSHIP:
            default:
                channel_fail_invalid_ownership(
                    "Unexpected INVALID_OWNERSHIP public disposition");
        }
        return ret_res.api_result;
    }

    // Phase 2: Exact Read Payload
    if (header_tmp.payload_len > 0) {
        channel_transfer_result p_res = channel_transfer_exact(entry, token, rec, REMEDY_WIRE_HEADER_SIZE, header_tmp.payload_len, channel_submit_kind::READ, 5000, tx);
        if (p_res.status != channel_transfer_status::COMPLETED || p_res.cumulative_transferred != header_tmp.payload_len) {
            {
                std::lock_guard<std::mutex> lock(entry->entry_mutex);
                if (p_res.status == channel_transfer_status::PENDING_UNRESOLVED) {
                    tx.primary_error = (p_res.last_error == ERROR_TIMEOUT) ? REMEDY_ERR_TIMEOUT : REMEDY_ERR_IPC_FAILURE;
                } else {
                    rec->completion_owner = channel_completion_owner::NONE_TERMINAL;
                    tx.primary_error = (p_res.last_error == ERROR_TIMEOUT) ? REMEDY_ERR_TIMEOUT : REMEDY_ERR_IPC_FAILURE;
                }
            }
            channel_public_return_result ret_res = channel_operation_epilogue(entry, token, rec, tx);
            switch (ret_res.disposition.value) {
                case channel_public_return_disposition::kind::NO_CURRENT_REQUEST:
                case channel_public_return_disposition::kind::TERMINAL_CURRENT_REQUEST:
                case channel_public_return_disposition::kind::DETACHED_CURRENT_REQUEST:
                    lease.release();
                    break;
                case channel_public_return_disposition::kind::INVALID_OWNERSHIP:
                default:
                    channel_fail_invalid_ownership(
                        "Unexpected INVALID_OWNERSHIP public disposition");
            }
            return ret_res.api_result;
        }
    }

    remedy_wire_frame_header_t header_final{};
    size_t payload_len_final = 0;
    const std::byte* payload_src_ptr = nullptr;

    {
        std::lock_guard<std::mutex> lock(entry->entry_mutex);

        bool tuple_ok = (rec->current_request_submitted == true &&
                         rec->current_request_pending == false &&
                         rec->current_request_completion_observed == true &&
                         rec->no_kernel_request_outstanding_at_public_return == true &&
                         rec->completion_owner == channel_completion_owner::PUBLIC_CALL &&
                         rec->state == channel_operation_state::CHUNK_COMPLETED);

        size_t expected_cum = (header_tmp.payload_len > 0) ? header_tmp.payload_len : REMEDY_WIRE_HEADER_SIZE;
        bool progress_ok = (rec->cumulative_target == expected_cum && rec->cumulative_transferred == expected_cum);

        bool buffer_ok = (REMEDY_WIRE_HEADER_SIZE <= rec->owned_buffer_size &&
                          header_tmp.payload_len <= (rec->owned_buffer_size - REMEDY_WIRE_HEADER_SIZE));

        if (!tuple_ok || !progress_ok || !buffer_ok) {
            rec->completion_owner = channel_completion_owner::NONE_TERMINAL;
            rec->state = channel_operation_state::COMPLETED;
            rec->completion_error = ERROR_INVALID_STATE;
            tx.primary_error = REMEDY_ERR_CONTAINMENT_FAILED;
        } else {
            header_final = header_tmp;
            payload_len_final = header_tmp.payload_len;
            payload_src_ptr = rec->owned_buffer + REMEDY_WIRE_HEADER_SIZE;
        }
    }

    if (tx.primary_error == REMEDY_ERR_CONTAINMENT_FAILED) {
        channel_public_return_result ret_res = channel_operation_epilogue(entry, token, rec, tx);
        switch (ret_res.disposition.value) {
            case channel_public_return_disposition::kind::NO_CURRENT_REQUEST:
            case channel_public_return_disposition::kind::TERMINAL_CURRENT_REQUEST:
            case channel_public_return_disposition::kind::DETACHED_CURRENT_REQUEST:
                lease.release();
                break;
            case channel_public_return_disposition::kind::INVALID_OWNERSHIP:
            default:
                channel_fail_invalid_ownership(
                    "Unexpected INVALID_OWNERSHIP public disposition");
        }
        return ret_res.api_result;
    }

    uint32_t actual_checksum =
        header_tmp.payload_len > 0
            ? remedy_adler32(
                  reinterpret_cast<const uint8_t*>(
                      rec->owned_buffer +
                      REMEDY_WIRE_HEADER_SIZE),
                  header_tmp.payload_len)
            : 0;

    if (actual_checksum != header_tmp.checksum) {
        {
            std::lock_guard<std::mutex> lock(entry->entry_mutex);
            rec->completion_owner =
                channel_completion_owner::NONE_TERMINAL;
            rec->state = channel_operation_state::COMPLETED;
            rec->completion_error = ERROR_CRC;
            tx.primary_error = REMEDY_ERR_IPC_FAILURE;
        }

        channel_public_return_result ret_res = channel_operation_epilogue(entry, token, rec, tx);
        switch (ret_res.disposition.value) {
            case channel_public_return_disposition::kind::NO_CURRENT_REQUEST:
            case channel_public_return_disposition::kind::TERMINAL_CURRENT_REQUEST:
            case channel_public_return_disposition::kind::DETACHED_CURRENT_REQUEST:
                lease.release();
                break;
            case channel_public_return_disposition::kind::INVALID_OWNERSHIP:
            default:
                channel_fail_invalid_ownership(
                    "Unexpected INVALID_OWNERSHIP public disposition");
        }
        return ret_res.api_result;
    }

    std::memcpy(out_header, &header_final, sizeof(remedy_wire_frame_header_t));
    if (payload_len_final > 0 && payload_buffer && payload_src_ptr) {
        std::memcpy(payload_buffer, payload_src_ptr, payload_len_final);
    }

    {
        std::lock_guard<std::mutex> lock(entry->entry_mutex);
        rec->completion_owner = channel_completion_owner::NONE_TERMINAL;
        rec->state = channel_operation_state::COMPLETED;
        tx.primary_error = REMEDY_OK;
    }

    channel_public_return_result ret_res = channel_operation_epilogue(entry, token, rec, tx);
    switch (ret_res.disposition.value) {
        case channel_public_return_disposition::kind::NO_CURRENT_REQUEST:
        case channel_public_return_disposition::kind::TERMINAL_CURRENT_REQUEST:
        case channel_public_return_disposition::kind::DETACHED_CURRENT_REQUEST:
            lease.release();
            break;
        case channel_public_return_disposition::kind::INVALID_OWNERSHIP:
        default:
            channel_fail_invalid_ownership(
                "Unexpected INVALID_OWNERSHIP public disposition");
    }
    return ret_res.api_result;
}

remedy_err_t channel_port_close(remedy_channel_token_t token) {
    if (token == REMEDY_INVALID_CHANNEL_TOKEN) return REMEDY_ERR_INVALID_ARGUMENT;
    win32_channel_entry* entry = nullptr; HANDLE hPipeCopy = INVALID_HANDLE_VALUE;
    {
        std::lock_guard<std::mutex> lock(g_channel_mutex);
        auto it = g_channel_table.find(token);
        if (it == g_channel_table.end()) return REMEDY_ERR_INVALID_ARGUMENT;
        entry = it->second;
        std::lock_guard<std::mutex> entry_lock(entry->entry_mutex);
        if (entry->state == channel_entry_state::CONSTRUCTING) return REMEDY_ERR_INVALID_ARGUMENT;
        if (entry->state == channel_entry_state::CLOSING && entry->finalizer_active) return REMEDY_OK;
        if (entry->state == channel_entry_state::LIVE) entry->state = channel_entry_state::CLOSING;
        if (entry->close_pins == UINT32_MAX) return REMEDY_ERR_OUT_OF_MEMORY;
        entry->close_pins++; hPipeCopy = entry->pipe_handle;
    }
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
    remedy_test_channel_check_close_pause_unlocked(token);
#endif
    remedy_err_t close_result = REMEDY_OK;
    if (hPipeCopy != INVALID_HANDLE_VALUE) {
        DWORD sim_canc_err = ERROR_SUCCESS;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
        remedy_test_channel_consume_pipe_cancel_injection(token, &sim_canc_err);
#endif
        channel_cancel_result cres = channel_cancel_io_checked(token, hPipeCopy, NULL, true, sim_canc_err);
        if (!cres.success) close_result = REMEDY_ERR_IPC_FAILURE;
    } else close_result = REMEDY_ERR_IPC_FAILURE;

    {
        std::lock_guard<std::mutex> elock(entry->entry_mutex);
        if (entry->close_pins > 0) { entry->close_pins--; entry->cv.notify_all(); }
    }
    return close_result;
}

remedy_err_t channel_port_destroy(remedy_channel_token_t token) {
    if (token == REMEDY_INVALID_CHANNEL_TOKEN) return REMEDY_ERR_INVALID_ARGUMENT;
    win32_channel_entry* entry = nullptr; uint64_t destroy_cookie = 0;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
    remedy_test_channel_take_destroy_call_cookie(&destroy_cookie);
#endif
    {
        std::lock_guard<std::mutex> lock(g_channel_mutex);
        auto it = g_channel_table.find(token);
        if (it == g_channel_table.end()) return REMEDY_ERR_INVALID_ARGUMENT;
        entry = it->second;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
        remedy_test_channel_increment_finalizer_invocations();
#endif
        entry->finalizer_ref_count.fetch_add(1, std::memory_order_relaxed);
    }

    remedy_err_t competitor_result = REMEDY_OK;
    bool is_competitor = false;
    uint64_t my_gen = 0;
    {
        std::unique_lock<std::mutex> elock(entry->entry_mutex);
        for (;;) {
            if (entry->finalizer_active) {
                uint64_t target_gen = entry->current_finalizer_generation;
                if (entry->finalizer_generation_waiters == UINT32_MAX) {
                    elock.unlock();
                    if (entry->finalizer_ref_count.fetch_sub(1, std::memory_order_acq_rel) == 1) {
                        delete entry;
                    }
                    return REMEDY_ERR_OUT_OF_MEMORY;
                }
                entry->finalizer_generation_waiters++;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
                remedy_test_channel_publish_competitor_attached(token, target_gen, destroy_cookie);
#endif
                entry->cv.wait(elock, [entry, target_gen]() {
                    return entry->active_finalizer_gen.generation_number == target_gen && entry->active_finalizer_gen.completed;
                });
                competitor_result = entry->active_finalizer_gen.final_result;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
                if (entry->finalizer_generation_waiters == 0) {
                    remedy_test_catastrophic_harness_failure("Underflow in finalizer_generation_waiters");
                }
#endif
                entry->finalizer_generation_waiters--;
                if (entry->finalizer_generation_waiters == 0) entry->cv.notify_all();
                is_competitor = true;
                break;
            }

            if (entry->active_finalizer_gen.completed && entry->finalizer_generation_waiters > 0) {
                entry->cv.wait(elock, [entry]() {
                    return entry->finalizer_generation_waiters == 0;
                });
                continue;
            }

            entry->finalizer_active = true;
            entry->state = channel_entry_state::CLOSING;
            my_gen = ++entry->current_finalizer_generation;
            entry->active_finalizer_gen = { my_gen, destroy_cookie, true, false, REMEDY_OK, true };
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
            remedy_test_channel_publish_finalizer_owner(token, destroy_cookie, my_gen, true, false, false, REMEDY_OK, true);
#endif
            break;
        }
    }

    if (is_competitor) {
        if (entry->finalizer_ref_count.fetch_sub(1, std::memory_order_acq_rel) == 1) {
            delete entry;
        }
        return competitor_result;
    }

#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
    remedy_test_channel_check_finalizer_pause(token);
    remedy_test_channel_publish_finalizer_draining(token, true);
#endif

    {
        std::unique_lock<std::mutex> elock(entry->entry_mutex);

        entry->cv.wait(elock, [entry]() {
            return entry->active_leases == 0 && entry->close_pins == 0;
        });

        channel_operation_record* slow_pre = entry->retained_events;
        channel_operation_record* fast_pre = entry->retained_events;
        uint32_t count_reserved = 0;
        bool pre_val_ok = true;
        while (slow_pre != nullptr) {
            if (slow_pre->detachment_reserved) {
                if (count_reserved == UINT32_MAX) { pre_val_ok = false; break; }
                count_reserved++;
            }
            slow_pre = slow_pre->next;
            if (fast_pre != nullptr && fast_pre->next != nullptr) {
                fast_pre = fast_pre->next->next;
                if (slow_pre == fast_pre && slow_pre != nullptr) { pre_val_ok = false; break; }
            }
        }
        if (!pre_val_ok || count_reserved != entry->detachment_reservations) {
            uint64_t current_gen = entry->active_finalizer_gen.generation_number;
            entry->active_finalizer_gen.completed = true;
            entry->active_finalizer_gen.active = false;
            entry->active_finalizer_gen.final_result = REMEDY_ERR_CONTAINMENT_FAILED;
            entry->active_finalizer_gen.entry_remains_registered = true;
            entry->finalizer_active = false;
            entry->cv.notify_all();
            elock.unlock();
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
            remedy_test_channel_publish_finalizer_owner(token, destroy_cookie, current_gen, false, false, true, REMEDY_ERR_CONTAINMENT_FAILED, true);
            remedy_test_channel_publish_finalizer_draining(token, false);
#endif
            if (entry->finalizer_ref_count.fetch_sub(1, std::memory_order_acq_rel) == 1) delete entry;
            return REMEDY_ERR_CONTAINMENT_FAILED;
        }

        channel_operation_record* chk = entry->retained_events;
        while (chk != nullptr) {
            if (chk->completion_owner == channel_completion_owner::PUBLIC_CALL) {
                uint64_t current_gen = entry->active_finalizer_gen.generation_number;
                entry->active_finalizer_gen.completed = true;
                entry->active_finalizer_gen.active = false;
                entry->active_finalizer_gen.final_result = REMEDY_ERR_CONTAINMENT_FAILED;
                entry->active_finalizer_gen.entry_remains_registered = true;
                entry->finalizer_active = false;
                entry->cv.notify_all();
                elock.unlock();
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
                remedy_test_channel_publish_finalizer_owner(token, destroy_cookie, current_gen, false, false, true, REMEDY_ERR_CONTAINMENT_FAILED, true);
                remedy_test_channel_publish_finalizer_draining(token, false);
#endif
                if (entry->finalizer_ref_count.fetch_sub(1, std::memory_order_acq_rel) == 1) delete entry;
                return REMEDY_ERR_CONTAINMENT_FAILED;
            }
            chk = chk->next;
        }

        bool detached_recovery_failed = false;
        remedy_err_t detached_recovery_error = REMEDY_OK;
        channel_operation_record* curr = entry->retained_events;
        while (curr != nullptr && !detached_recovery_failed) {
            if (curr->state == channel_operation_state::DETACHED_PENDING) {
                if (curr->completion_owner != channel_completion_owner::DETACHED_FINALIZER ||
                    curr->detachment_reserved ||
                    !curr->current_request_submitted ||
                    !curr->current_request_pending ||
                    curr->current_request_completion_observed ||
                    curr->no_kernel_request_outstanding_at_public_return ||
                    entry->detached_pending_operations == 0 ||
                    curr->event_handle == NULL || curr->event_handle == INVALID_HANDLE_VALUE ||
                    entry->pipe_handle == NULL || entry->pipe_handle == INVALID_HANDLE_VALUE) {
                    detached_recovery_failed = true;
                    detached_recovery_error = REMEDY_ERR_CONTAINMENT_FAILED;
                    break;
                }

                uint64_t target_inst = curr->instance_id;
                HANDLE pipe_h = entry->pipe_handle;
                OVERLAPPED* ov_ptr = &curr->overlapped;
                HANDLE ev_h = curr->event_handle;

                elock.unlock();

                DWORD sim_canc_err = ERROR_SUCCESS;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
                remedy_test_channel_consume_operation_cancel_injection(token, (uint32_t)curr->kind, &sim_canc_err);
#endif
                channel_cancel_result cres = channel_cancel_io_checked(token, pipe_h, ov_ptr, false, sim_canc_err);
                channel_wait_result wres = channel_wait_once_classified(token, curr->kind, ev_h, 50);

                DWORD transferred = 0;
                BOOL get_ok = GetOverlappedResult(pipe_h, ov_ptr, &transferred, FALSE);
                DWORD comp_err = get_ok ? ERROR_SUCCESS : GetLastError();

                elock.lock();

                channel_operation_record* slow_post = entry->retained_events;
                channel_operation_record* fast_post = entry->retained_events;
                uint32_t inst_occurrences = 0;
                bool post_val_ok = true;
                while (slow_post != nullptr) {
                    if (slow_post == curr && slow_post->instance_id == target_inst) inst_occurrences++;
                    slow_post = slow_post->next;
                    if (fast_post != nullptr && fast_post->next != nullptr) {
                        fast_post = fast_post->next->next;
                        if (slow_post == fast_post && slow_post != nullptr) { post_val_ok = false; break; }
                    }
                }
                if (!post_val_ok || inst_occurrences != 1 ||
                    curr->state != channel_operation_state::DETACHED_PENDING ||
                    curr->completion_owner != channel_completion_owner::DETACHED_FINALIZER ||
                    curr->detachment_reserved ||
                    !curr->current_request_submitted ||
                    !curr->current_request_pending ||
                    curr->current_request_completion_observed ||
                    curr->no_kernel_request_outstanding_at_public_return ||
                    curr->event_handle != ev_h ||
                    &curr->overlapped != ov_ptr ||
                    entry->pipe_handle != pipe_h ||
                    entry->detached_pending_operations == 0) {
                    detached_recovery_failed = true;
                    detached_recovery_error = REMEDY_ERR_CONTAINMENT_FAILED;
                    break;
                }

                if ((curr->cancellation_failed && !curr->cancellation_failed_observed_by_destroy) || !cres.success || wres.reason == channel_wait_reason::WAIT_REASON_FAILED) {
                    curr->cancellation_failed_observed_by_destroy = true;
                    curr->cancellation_error = (!cres.success) ? cres.win32_error : wres.win32_error;
                    curr->completion_error = wres.win32_error;
                    detached_recovery_failed = true;
                    detached_recovery_error = REMEDY_ERR_IPC_FAILURE;
                    break;
                }

                bool completion_success = (get_ok != FALSE);
                bool completion_still_pending = !completion_success && (comp_err == ERROR_IO_INCOMPLETE || comp_err == ERROR_IO_PENDING);
                bool invalid_completion_observation = !completion_success && (comp_err == ERROR_SUCCESS || comp_err == ERROR_INVALID_HANDLE || comp_err == ERROR_INVALID_PARAMETER || comp_err == ERROR_INVALID_STATE);
                bool terminal_operation_failure = !completion_success && !completion_still_pending && !invalid_completion_observation;

                if (completion_success || terminal_operation_failure) {
                    curr->current_request_submitted = true;
                    curr->current_request_pending = false;
                    curr->current_request_completion_observed = true;
                    curr->ever_completion_observed = true;
                    curr->no_kernel_request_outstanding_at_public_return = true;
                    curr->completion_owner = channel_completion_owner::NONE_TERMINAL;
                    curr->state = channel_operation_state::COMPLETED;
                    curr->completion_error = comp_err;
                    curr->transferred_size = transferred;
                    curr->detached_recovered = true;
                    entry->detached_pending_operations--;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
                    remedy_test_channel_increment_detached_recovered();
#endif
                } else if (completion_still_pending) {
                    curr->final_completion_error = wres.win32_error;
                    curr->cancellation_error = (!cres.success) ? cres.win32_error : wres.win32_error;
                    curr->completion_error = comp_err;
                    detached_recovery_failed = true;
                    detached_recovery_error = REMEDY_ERR_IPC_FAILURE;
                    break;
                } else {
                    curr->cancellation_error = (!cres.success) ? cres.win32_error : comp_err;
                    curr->completion_error = comp_err;
                    detached_recovery_failed = true;
                    detached_recovery_error = REMEDY_ERR_CONTAINMENT_FAILED;
                    break;
                }
            }
            curr = curr->next;
        }

        if (detached_recovery_failed) {
            uint64_t current_gen = entry->active_finalizer_gen.generation_number;
            entry->active_finalizer_gen.completed = true;
            entry->active_finalizer_gen.active = false;
            entry->active_finalizer_gen.final_result = detached_recovery_error;
            entry->active_finalizer_gen.entry_remains_registered = true;
            entry->finalizer_active = false;
            entry->cv.notify_all();
            elock.unlock();
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
            remedy_test_channel_publish_finalizer_owner(token, destroy_cookie, current_gen, false, false, true, detached_recovery_error, true);
            remedy_test_channel_publish_finalizer_draining(token, false);
#endif
            if (entry->finalizer_ref_count.fetch_sub(1, std::memory_order_acq_rel) == 1) delete entry;
            return detached_recovery_error;
        }

        bool retirement_failed = false;
        while (entry->retained_events != nullptr) {
            channel_operation_record* rec_to_retire = entry->retained_events;
            if (rec_to_retire->completion_owner != channel_completion_owner::NONE_TERMINAL ||
                !rec_to_retire->no_kernel_request_outstanding_at_public_return) {
                retirement_failed = true;
                break;
            }
            if (rec_to_retire->state == channel_operation_state::RETIREMENT_FAILED && !rec_to_retire->event_close_failure_observed_by_destroy) {
                rec_to_retire->event_close_failure_observed_by_destroy = true;
                retirement_failed = true;
                break;
            }
            channel_record_retire_status rstat = channel_retire_completed_record_locked(entry, token, rec_to_retire);
            if (rstat.result != channel_record_retire_result::RETIRED) {
                if (rstat.result == channel_record_retire_result::RETAINED_CLOSE_FAILURE) {
                    rec_to_retire->event_close_failure_observed_by_destroy = true;
                }
                retirement_failed = true;
                break;
            }
        }

        if (retirement_failed || entry->detachment_reservations != 0 || entry->detached_pending_operations != 0 || entry->retained_events != nullptr) {
            uint64_t current_gen = entry->active_finalizer_gen.generation_number;
            entry->active_finalizer_gen.completed = true;
            entry->active_finalizer_gen.active = false;
            entry->active_finalizer_gen.final_result = REMEDY_ERR_IPC_FAILURE;
            entry->active_finalizer_gen.entry_remains_registered = true;
            entry->finalizer_active = false;
            entry->cv.notify_all();
            elock.unlock();
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
            remedy_test_channel_publish_finalizer_owner(token, destroy_cookie, current_gen, false, false, true, REMEDY_ERR_IPC_FAILURE, true);
            remedy_test_channel_publish_finalizer_draining(token, false);
#endif
            if (entry->finalizer_ref_count.fetch_sub(1, std::memory_order_acq_rel) == 1) delete entry;
            return REMEDY_ERR_IPC_FAILURE;
        }

        if (entry->pipe_handle != INVALID_HANDLE_VALUE) {
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
            if (remedy_test_channel_check_pipe_close_injection(token)) {
                uint64_t current_gen = entry->active_finalizer_gen.generation_number;
                entry->active_finalizer_gen.completed = true;
                entry->active_finalizer_gen.active = false;
                entry->active_finalizer_gen.final_result = REMEDY_ERR_IPC_FAILURE;
                entry->active_finalizer_gen.entry_remains_registered = true;
                entry->finalizer_active = false;
                entry->cv.notify_all();
                elock.unlock();
                remedy_test_channel_publish_finalizer_owner(token, destroy_cookie, current_gen, false, false, true, REMEDY_ERR_IPC_FAILURE, true);
                remedy_test_channel_publish_finalizer_draining(token, false);
                if (entry->finalizer_ref_count.fetch_sub(1, std::memory_order_acq_rel) == 1) delete entry;
                return REMEDY_ERR_IPC_FAILURE;
            }
#endif
            BOOL pipe_close_ok = CloseHandle(entry->pipe_handle);
            if (!pipe_close_ok) {
                uint64_t current_gen = entry->active_finalizer_gen.generation_number;
                entry->active_finalizer_gen.completed = true;
                entry->active_finalizer_gen.active = false;
                entry->active_finalizer_gen.final_result = REMEDY_ERR_IPC_FAILURE;
                entry->active_finalizer_gen.entry_remains_registered = true;
                entry->finalizer_active = false;
                entry->cv.notify_all();
                elock.unlock();

#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
                remedy_test_channel_publish_finalizer_owner(token, destroy_cookie, current_gen, false, false, true, REMEDY_ERR_IPC_FAILURE, true);
                remedy_test_channel_publish_finalizer_draining(token, false);
#endif
                if (entry->finalizer_ref_count.fetch_sub(1, std::memory_order_acq_rel) == 1) delete entry;
                return REMEDY_ERR_IPC_FAILURE;
            }
            entry->pipe_handle = INVALID_HANDLE_VALUE;
#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
            remedy_test_channel_increment_pipe_closures();
#endif
        }
    }

    uint64_t final_gen = 0;
    {
        std::lock_guard<std::mutex> lock(g_channel_mutex);
        g_channel_table.erase(token);
        entry->finalizer_ref_count.fetch_sub(1, std::memory_order_relaxed);
        {
            std::lock_guard<std::mutex> elock(entry->entry_mutex);
            entry->state = channel_entry_state::RETIRED;
            entry->finalizer_active = false;
            final_gen = entry->active_finalizer_gen.generation_number;
            entry->active_finalizer_gen.completed = true;
            entry->active_finalizer_gen.active = false;
            entry->active_finalizer_gen.final_result = REMEDY_OK;
            entry->active_finalizer_gen.entry_remains_registered = false;
            entry->cv.notify_all();
        }
    }

#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
    remedy_test_channel_increment_entry_deletions();
    remedy_test_channel_increment_finalizer_completions();
    remedy_test_channel_increment_successful_finalizers();
    remedy_test_channel_publish_finalizer_owner(token, destroy_cookie, final_gen, false, false, true, REMEDY_OK, false);
    remedy_test_channel_publish_finalizer_draining(token, false);
#endif

    if (entry->finalizer_ref_count.fetch_sub(1, std::memory_order_acq_rel) == 1) {
        delete entry;
    }
    return REMEDY_OK;
}

} // extern "C"

#ifdef REMEDY_TEST_CHANNEL_LIFETIME_SEAM
static std::mutex g_test_seam_mutex;
static std::condition_variable g_test_seam_cv;
static std::map<std::tuple<remedy_channel_token_t, uint32_t, uint64_t>, remedy_test_channel_operation_history> g_test_history_store;
static std::map<remedy_channel_token_t, channel_finalizer_owner_observation> g_test_finalizer_owner_map;

thread_local uint64_t g_last_created_operation_instance_id{0};
thread_local uint64_t g_test_operation_call_cookie{0};
thread_local uint64_t g_test_destroy_call_cookie{0};

static std::map<std::tuple<remedy_channel_token_t, uint32_t, uint64_t>, uint64_t> g_test_op_created_map;
static std::map<std::tuple<remedy_channel_token_t, uint32_t, uint64_t>, bool> g_test_op_pending_map;

static std::map<std::tuple<remedy_channel_token_t, uint32_t>, DWORD> g_test_wait_failure_injection_map;
static std::map<remedy_channel_token_t, DWORD> g_test_pipe_cancel_injection_map;
static std::map<std::tuple<remedy_channel_token_t, uint32_t>, DWORD> g_test_op_cancel_injection_map;

struct test_lease_pause_state { remedy_channel_token_t token{REMEDY_INVALID_CHANNEL_TOKEN}; bool armed{false}; bool paused{false}; bool release_requested{false}; };
struct test_submission_pause_state { remedy_channel_token_t token{REMEDY_INVALID_CHANNEL_TOKEN}; uint32_t kind{0}; uint64_t cookie{0}; uint64_t instance{0}; bool armed{false}; bool paused{false}; bool release_requested{false}; };
struct test_finalizer_pause_state { remedy_channel_token_t token{REMEDY_INVALID_CHANNEL_TOKEN}; bool armed{false}; bool paused{false}; bool release_requested{false}; };
struct test_close_pause_state { remedy_channel_token_t token{REMEDY_INVALID_CHANNEL_TOKEN}; bool armed{false}; bool paused{false}; bool release_requested{false}; };

static test_lease_pause_state g_lease_pause{};
static test_submission_pause_state g_sub_pause{};
static test_finalizer_pause_state g_fin_pause{};
static test_close_pause_state g_close_pause{};

static std::map<remedy_channel_token_t, bool> g_test_finalizer_draining_map;

static DWORD g_test_pipe_creation_injection_store{ERROR_SUCCESS};
static std::map<remedy_channel_token_t, bool> g_test_pipe_close_injection_map;
static std::map<std::tuple<remedy_channel_token_t, uint32_t>, bool> g_test_event_close_injection_map;
static std::map<std::tuple<remedy_channel_token_t, uint64_t>, uint64_t> g_test_competitor_attached_map;

static channel_test_counts g_monotonic_counts{};

static bool get_exact_armed_injection_count_locked(uint32_t* out_count) {
    if (!out_count) return false;
    uint64_t total = 0;
    size_t sizes[] = {
        g_test_wait_failure_injection_map.size(),
        g_test_pipe_cancel_injection_map.size(),
        g_test_op_cancel_injection_map.size(),
        (g_test_pipe_creation_injection_store != ERROR_SUCCESS) ? static_cast<size_t>(1) : static_cast<size_t>(0),
        g_test_pipe_close_injection_map.size(),
        g_test_event_close_injection_map.size()
    };
    for (size_t s : sizes) {
        if (s > UINT32_MAX - total) return false;
        total += static_cast<uint64_t>(s);
    }
    *out_count = static_cast<uint32_t>(total);
    return true;
}

extern "C" {
    [[noreturn]] void remedy_test_catastrophic_harness_failure(const char* message) {
        std::fprintf(stderr, "CATASTROPHIC TEST HARNESS FAILURE: %s\n", message);
        std::fflush(stderr);
        std::_Exit(1);
    }

    bool remedy_test_channel_reset_seam(void) {
        std::lock_guard<std::mutex> lock(g_test_seam_mutex);
        g_test_history_store.clear(); g_test_finalizer_owner_map.clear();
        g_last_created_operation_instance_id = 0; g_test_operation_call_cookie = 0; g_test_destroy_call_cookie = 0;
        g_test_op_created_map.clear(); g_test_op_pending_map.clear();
        g_test_wait_failure_injection_map.clear();
        g_test_pipe_cancel_injection_map.clear();
        g_test_op_cancel_injection_map.clear();
        g_lease_pause = {}; g_sub_pause = {}; g_fin_pause = {}; g_close_pause = {};
        g_test_finalizer_draining_map.clear();
        g_test_pipe_creation_injection_store = ERROR_SUCCESS;
        g_test_pipe_close_injection_map.clear();
        g_test_event_close_injection_map.clear();
        g_test_competitor_attached_map.clear();
        g_monotonic_counts = {};
        g_test_seam_cv.notify_all();
        return true;
    }

    bool remedy_test_channel_arm_lease_pause(remedy_channel_token_t token) {
        if (token == REMEDY_INVALID_CHANNEL_TOKEN) return false;
        std::lock_guard<std::mutex> lock(g_test_seam_mutex);
        if (g_lease_pause.armed) return false;
        g_lease_pause = {token, true, false, false};
        return true;
    }
    bool remedy_test_channel_wait_lease_paused(remedy_channel_token_t token, uint32_t timeout_ms) {
        std::unique_lock<std::mutex> lock(g_test_seam_mutex);
        bool res = g_test_seam_cv.wait_for(lock, std::chrono::milliseconds(timeout_ms), [token]() {
            return !g_lease_pause.armed || (g_lease_pause.token == token && g_lease_pause.paused);
        });
        return res && g_lease_pause.armed && g_lease_pause.token == token && g_lease_pause.paused;
    }
    bool remedy_test_channel_release_lease_pause(void) {
        std::unique_lock<std::mutex> lock(g_test_seam_mutex);

        if (!g_lease_pause.armed) {
            return false;
        }

        if (!g_lease_pause.paused) {
            g_lease_pause = {};
            g_test_seam_cv.notify_all();
            return true;
        }

        g_lease_pause.release_requested = true;
        g_test_seam_cv.notify_all();

        bool cleared = g_test_seam_cv.wait_for(
            lock,
            std::chrono::milliseconds(1000),
            []() { return !g_lease_pause.armed; });

        if (!cleared) {
            remedy_test_catastrophic_harness_failure(
                "Timed out releasing lease pause");
        }

        return true;
    }
    bool remedy_test_channel_check_lease_pause_locked(remedy_channel_token_t token) {
        std::unique_lock<std::mutex> lock(g_test_seam_mutex);
        if (!g_lease_pause.armed || g_lease_pause.token != token) return false;
        g_lease_pause.paused = true;
        g_test_seam_cv.notify_all();
        g_test_seam_cv.wait(lock, []() { return !g_lease_pause.armed || g_lease_pause.release_requested; });
        if (!g_lease_pause.armed) return false;
        g_lease_pause = {};
        g_test_seam_cv.notify_all();
        return true;
    }

    bool remedy_test_channel_arm_submission_pause(remedy_channel_token_t token, uint32_t operation_kind, uint64_t call_cookie) {
        if (token == REMEDY_INVALID_CHANNEL_TOKEN) return false;
        if (operation_kind != 1 && operation_kind != 2 && operation_kind != 3) return false;
        std::lock_guard<std::mutex> lock(g_test_seam_mutex);
        if (g_sub_pause.armed) return false;
        g_sub_pause = {token, operation_kind, call_cookie, 0, true, false, false};
        return true;
    }
    bool remedy_test_channel_wait_submission_paused(remedy_channel_token_t token, uint32_t operation_kind, uint64_t call_cookie, uint32_t timeout_ms, uint64_t* out_instance_id) {
        if (!out_instance_id) return false;
        std::unique_lock<std::mutex> lock(g_test_seam_mutex);
        bool res = g_test_seam_cv.wait_for(lock, std::chrono::milliseconds(timeout_ms), [token, operation_kind, call_cookie]() {
            return !g_sub_pause.armed || (g_sub_pause.token == token && g_sub_pause.kind == operation_kind && g_sub_pause.cookie == call_cookie && g_sub_pause.paused);
        });
        if (res && g_sub_pause.armed) *out_instance_id = g_sub_pause.instance;
        return res && g_sub_pause.armed && g_sub_pause.token == token && g_sub_pause.kind == operation_kind && g_sub_pause.cookie == call_cookie && g_sub_pause.paused;
    }
    bool remedy_test_channel_release_submission_pause(void) {
        std::unique_lock<std::mutex> lock(g_test_seam_mutex);

        if (!g_sub_pause.armed) {
            return false;
        }

        if (!g_sub_pause.paused) {
            g_sub_pause = {};
            g_test_seam_cv.notify_all();
            return true;
        }

        g_sub_pause.release_requested = true;
        g_test_seam_cv.notify_all();

        bool cleared = g_test_seam_cv.wait_for(
            lock,
            std::chrono::milliseconds(1000),
            []() { return !g_sub_pause.armed; });

        if (!cleared) {
            remedy_test_catastrophic_harness_failure(
                "Timed out releasing submission pause");
        }

        return true;
    }
    bool remedy_test_channel_check_submission_pause_unlocked(remedy_channel_token_t token, uint32_t operation_kind, uint64_t call_cookie, uint64_t instance_id) {
        std::unique_lock<std::mutex> lock(g_test_seam_mutex);
        if (!g_sub_pause.armed || g_sub_pause.token != token || g_sub_pause.kind != operation_kind || g_sub_pause.cookie != call_cookie) return false;
        g_sub_pause.instance = instance_id;
        g_sub_pause.paused = true;
        g_test_seam_cv.notify_all();
        g_test_seam_cv.wait(lock, []() { return !g_sub_pause.armed || g_sub_pause.release_requested; });
        if (!g_sub_pause.armed) return false;
        g_sub_pause = {};
        g_test_seam_cv.notify_all();
        return true;
    }

    bool remedy_test_channel_arm_finalizer_pause(remedy_channel_token_t token) {
        if (token == REMEDY_INVALID_CHANNEL_TOKEN) return false;
        std::lock_guard<std::mutex> lock(g_test_seam_mutex);
        if (g_fin_pause.armed) return false;
        g_fin_pause = {token, true, false, false};
        return true;
    }
    bool remedy_test_channel_wait_finalizer_paused(remedy_channel_token_t token, uint32_t timeout_ms) {
        std::unique_lock<std::mutex> lock(g_test_seam_mutex);
        bool res = g_test_seam_cv.wait_for(lock, std::chrono::milliseconds(timeout_ms), [token]() {
            return !g_fin_pause.armed || (g_fin_pause.token == token && g_fin_pause.paused);
        });
        return res && g_fin_pause.armed && g_fin_pause.token == token && g_fin_pause.paused;
    }
    bool remedy_test_channel_release_finalizer_pause(void) {
        std::unique_lock<std::mutex> lock(g_test_seam_mutex);

        if (!g_fin_pause.armed) {
            return false;
        }

        if (!g_fin_pause.paused) {
            g_fin_pause = {};
            g_test_seam_cv.notify_all();
            return true;
        }

        g_fin_pause.release_requested = true;
        g_test_seam_cv.notify_all();

        bool cleared = g_test_seam_cv.wait_for(
            lock,
            std::chrono::milliseconds(1000),
            []() { return !g_fin_pause.armed; });

        if (!cleared) {
            remedy_test_catastrophic_harness_failure(
                "Timed out releasing finalizer pause");
        }

        return true;
    }
    bool remedy_test_channel_check_finalizer_pause(remedy_channel_token_t token) {
        std::unique_lock<std::mutex> lock(g_test_seam_mutex);
        if (!g_fin_pause.armed || g_fin_pause.token != token) return false;
        g_fin_pause.paused = true;
        g_test_seam_cv.notify_all();
        g_test_seam_cv.wait(lock, []() { return !g_fin_pause.armed || g_fin_pause.release_requested; });
        if (!g_fin_pause.armed) return false;
        g_fin_pause = {};
        g_test_seam_cv.notify_all();
        return true;
    }
    bool remedy_test_channel_wait_finalizer_draining(remedy_channel_token_t token, uint32_t timeout_ms) {
        std::unique_lock<std::mutex> lock(g_test_seam_mutex);
        return g_test_seam_cv.wait_for(lock, std::chrono::milliseconds(timeout_ms), [token]() {
            auto it = g_test_finalizer_draining_map.find(token);
            return (it != g_test_finalizer_draining_map.end() && it->second);
        });
    }
    bool remedy_test_channel_publish_finalizer_draining(remedy_channel_token_t token, bool is_draining) {
        std::lock_guard<std::mutex> lock(g_test_seam_mutex);
        g_test_finalizer_draining_map[token] = is_draining;
        g_test_seam_cv.notify_all();
        return true;
    }

    bool remedy_test_channel_arm_close_pause(remedy_channel_token_t token) {
        if (token == REMEDY_INVALID_CHANNEL_TOKEN) return false;
        std::lock_guard<std::mutex> lock(g_test_seam_mutex);
        if (g_close_pause.armed) return false;
        g_close_pause = {token, true, false, false};
        return true;
    }
    bool remedy_test_channel_wait_close_paused(remedy_channel_token_t token, uint32_t timeout_ms) {
        std::unique_lock<std::mutex> lock(g_test_seam_mutex);
        bool res = g_test_seam_cv.wait_for(lock, std::chrono::milliseconds(timeout_ms), [token]() {
            return !g_close_pause.armed || (g_close_pause.token == token && g_close_pause.paused);
        });
        return res && g_close_pause.armed && g_close_pause.token == token && g_close_pause.paused;
    }
    bool remedy_test_channel_release_close_pause(void) {
        std::unique_lock<std::mutex> lock(g_test_seam_mutex);

        if (!g_close_pause.armed) {
            return false;
        }

        if (!g_close_pause.paused) {
            g_close_pause = {};
            g_test_seam_cv.notify_all();
            return true;
        }

        g_close_pause.release_requested = true;
        g_test_seam_cv.notify_all();

        bool cleared = g_test_seam_cv.wait_for(
            lock,
            std::chrono::milliseconds(1000),
            []() { return !g_close_pause.armed; });

        if (!cleared) {
            remedy_test_catastrophic_harness_failure(
                "Timed out releasing close pause");
        }

        return true;
    }
    bool remedy_test_channel_check_close_pause_unlocked(remedy_channel_token_t token) {
        std::unique_lock<std::mutex> lock(g_test_seam_mutex);
        if (!g_close_pause.armed || g_close_pause.token != token) return false;
        g_close_pause.paused = true;
        g_test_seam_cv.notify_all();
        g_test_seam_cv.wait(lock, []() { return !g_close_pause.armed || g_close_pause.release_requested; });
        if (!g_close_pause.armed) return false;
        g_close_pause = {};
        g_test_seam_cv.notify_all();
        return true;
    }

    bool remedy_test_channel_inject_pipe_creation_failure(DWORD sim_error) {
        if (sim_error == ERROR_SUCCESS) return false;
        std::lock_guard<std::mutex> lock(g_test_seam_mutex);
        uint32_t current_count = 0;
        if (!get_exact_armed_injection_count_locked(&current_count) || current_count == UINT32_MAX) return false;
        if (g_test_pipe_creation_injection_store != ERROR_SUCCESS) return false;
        g_test_pipe_creation_injection_store = sim_error;
        return true;
    }
    bool remedy_test_channel_check_pipe_creation_injection(DWORD* out_sim_error) {
        if (!out_sim_error) return false;
        std::lock_guard<std::mutex> lock(g_test_seam_mutex);
        if (g_test_pipe_creation_injection_store == ERROR_SUCCESS) return false;
        *out_sim_error = g_test_pipe_creation_injection_store;
        g_test_pipe_creation_injection_store = ERROR_SUCCESS;
        return true;
    }

    bool remedy_test_channel_inject_pipe_close_failure(remedy_channel_token_t token) {
        if (token == REMEDY_INVALID_CHANNEL_TOKEN) return false;
        std::lock_guard<std::mutex> lock(g_test_seam_mutex);
        uint32_t current_count = 0;
        if (!get_exact_armed_injection_count_locked(&current_count) || current_count == UINT32_MAX) return false;
        if (g_test_pipe_close_injection_map.find(token) != g_test_pipe_close_injection_map.end()) return false;
        g_test_pipe_close_injection_map[token] = true;
        return true;
    }
    bool remedy_test_channel_check_pipe_close_injection(remedy_channel_token_t token) {
        std::lock_guard<std::mutex> lock(g_test_seam_mutex);
        auto it = g_test_pipe_close_injection_map.find(token);
        if (it == g_test_pipe_close_injection_map.end()) return false;
        g_test_pipe_close_injection_map.erase(it);
        g_monotonic_counts.pipe_close_failures++;
        return true;
    }

    bool remedy_test_channel_inject_event_close_failure(remedy_channel_token_t token, uint32_t operation_kind) {
        if (token == REMEDY_INVALID_CHANNEL_TOKEN) return false;
        if (operation_kind != 1 && operation_kind != 2 && operation_kind != 3) return false;
        std::lock_guard<std::mutex> lock(g_test_seam_mutex);
        uint32_t current_count = 0;
        if (!get_exact_armed_injection_count_locked(&current_count) || current_count == UINT32_MAX) return false;
        auto key = std::make_tuple(token, operation_kind);
        if (g_test_event_close_injection_map.find(key) != g_test_event_close_injection_map.end()) return false;
        g_test_event_close_injection_map[key] = true;
        return true;
    }
    bool remedy_test_channel_check_event_close_injection(remedy_channel_token_t token, uint32_t operation_kind) {
        std::lock_guard<std::mutex> lock(g_test_seam_mutex);
        auto key = std::make_tuple(token, operation_kind);
        auto it = g_test_event_close_injection_map.find(key);
        if (it == g_test_event_close_injection_map.end()) return false;
        g_test_event_close_injection_map.erase(it);
        g_monotonic_counts.event_close_failures++;
        return true;
    }

    bool remedy_test_channel_inject_wait_failure(remedy_channel_token_t token, uint32_t operation_kind, DWORD simulated_error) {
        if (token == REMEDY_INVALID_CHANNEL_TOKEN || simulated_error == ERROR_SUCCESS) return false;
        if (operation_kind != 1 && operation_kind != 2 && operation_kind != 3) return false;
        std::lock_guard<std::mutex> lock(g_test_seam_mutex);
        uint32_t current_count = 0;
        if (!get_exact_armed_injection_count_locked(&current_count) || current_count == UINT32_MAX) return false;
        auto key = std::make_tuple(token, operation_kind);
        if (g_test_wait_failure_injection_map.find(key) != g_test_wait_failure_injection_map.end()) return false;
        g_test_wait_failure_injection_map[key] = simulated_error;
        return true;
    }

    bool remedy_test_channel_consume_wait_failure_injection(remedy_channel_token_t token, uint32_t operation_kind, DWORD* out_simulated_error) {
        if (!out_simulated_error) return false;
        std::lock_guard<std::mutex> lock(g_test_seam_mutex);
        auto key = std::make_tuple(token, operation_kind);
        auto it = g_test_wait_failure_injection_map.find(key);
        if (it == g_test_wait_failure_injection_map.end()) return false;
        *out_simulated_error = it->second;
        g_test_wait_failure_injection_map.erase(it);
        g_monotonic_counts.wait_failures++;
        return true;
    }

    bool remedy_test_channel_inject_pipe_cancel_failure(remedy_channel_token_t token, DWORD simulated_error) {
        if (token == REMEDY_INVALID_CHANNEL_TOKEN || simulated_error == ERROR_SUCCESS) return false;
        std::lock_guard<std::mutex> lock(g_test_seam_mutex);
        uint32_t current_count = 0;
        if (!get_exact_armed_injection_count_locked(&current_count) || current_count == UINT32_MAX) return false;
        if (g_test_pipe_cancel_injection_map.find(token) != g_test_pipe_cancel_injection_map.end()) return false;
        g_test_pipe_cancel_injection_map[token] = simulated_error;
        return true;
    }

    bool remedy_test_channel_consume_pipe_cancel_injection(remedy_channel_token_t token, DWORD* out_simulated_error) {
        if (!out_simulated_error) return false;
        std::lock_guard<std::mutex> lock(g_test_seam_mutex);
        auto it = g_test_pipe_cancel_injection_map.find(token);
        if (it == g_test_pipe_cancel_injection_map.end()) return false;
        *out_simulated_error = it->second;
        g_test_pipe_cancel_injection_map.erase(it);
        g_monotonic_counts.pipe_cancel_failures++;
        return true;
    }

    bool remedy_test_channel_inject_operation_cancel_failure(remedy_channel_token_t token, uint32_t operation_kind, DWORD simulated_error) {
        if (token == REMEDY_INVALID_CHANNEL_TOKEN || simulated_error == ERROR_SUCCESS) return false;
        if (operation_kind != 1 && operation_kind != 2 && operation_kind != 3) return false;
        std::lock_guard<std::mutex> lock(g_test_seam_mutex);
        uint32_t current_count = 0;
        if (!get_exact_armed_injection_count_locked(&current_count) || current_count == UINT32_MAX) return false;
        auto key = std::make_tuple(token, operation_kind);
        if (g_test_op_cancel_injection_map.find(key) != g_test_op_cancel_injection_map.end()) return false;
        g_test_op_cancel_injection_map[key] = simulated_error;
        return true;
    }

    bool remedy_test_channel_consume_operation_cancel_injection(remedy_channel_token_t token, uint32_t operation_kind, DWORD* out_simulated_error) {
        if (!out_simulated_error) return false;
        std::lock_guard<std::mutex> lock(g_test_seam_mutex);
        auto key = std::make_tuple(token, operation_kind);
        auto it = g_test_op_cancel_injection_map.find(key);
        if (it == g_test_op_cancel_injection_map.end()) return false;
        *out_simulated_error = it->second;
        g_test_op_cancel_injection_map.erase(it);
        g_monotonic_counts.operation_cancel_failures++;
        return true;
    }

    bool remedy_test_channel_clear_last_created_operation_instance(void) { g_last_created_operation_instance_id = 0; return true; }
    bool remedy_test_channel_publish_last_created_operation_instance(uint64_t instance_id) { g_last_created_operation_instance_id = instance_id; return true; }
    bool remedy_test_channel_take_last_created_operation_instance(uint64_t* out_instance_id) {
        if (!out_instance_id) return false;
        *out_instance_id = g_last_created_operation_instance_id;
        g_last_created_operation_instance_id = 0;
        return true;
    }

    bool remedy_test_channel_set_operation_call_cookie(uint64_t cookie) { g_test_operation_call_cookie = cookie; return true; }
    bool remedy_test_channel_take_operation_call_cookie(uint64_t* out_cookie) {
        if (!out_cookie) return false; *out_cookie = g_test_operation_call_cookie; g_test_operation_call_cookie = 0; return true;
    }
    bool remedy_test_channel_set_destroy_call_cookie(uint64_t cookie) { g_test_destroy_call_cookie = cookie; return true; }
    bool remedy_test_channel_take_destroy_call_cookie(uint64_t* out_cookie) {
        if (!out_cookie) return false; *out_cookie = g_test_destroy_call_cookie; g_test_destroy_call_cookie = 0; return true;
    }

    bool remedy_test_channel_publish_operation_created(remedy_channel_token_t token, uint32_t operation_kind, uint64_t call_cookie, uint64_t instance_id) {
        std::lock_guard<std::mutex> lock(g_test_seam_mutex);
        g_test_op_created_map[{token, operation_kind, call_cookie}] = instance_id;
        g_test_seam_cv.notify_all();
        return true;
    }

    bool remedy_test_channel_wait_operation_created(remedy_channel_token_t token, uint32_t operation_kind, uint64_t call_cookie, uint32_t timeout_ms, uint64_t* out_instance_id) {
        if (!out_instance_id) return false;
        std::unique_lock<std::mutex> lock(g_test_seam_mutex);
        bool res = g_test_seam_cv.wait_for(lock, std::chrono::milliseconds(timeout_ms), [token, operation_kind, call_cookie]() {
            auto it = g_test_op_created_map.find({token, operation_kind, call_cookie});
            return (it != g_test_op_created_map.end() && it->second != 0);
        });
        if (res) *out_instance_id = g_test_op_created_map[{token, operation_kind, call_cookie}];
        return res;
    }

    bool remedy_test_channel_publish_operation_pending(remedy_channel_token_t token, uint32_t operation_kind, uint64_t instance_id, bool is_pending) {
        std::lock_guard<std::mutex> lock(g_test_seam_mutex);
        g_test_op_pending_map[{token, operation_kind, instance_id}] = is_pending;
        g_test_seam_cv.notify_all();
        return true;
    }

    bool remedy_test_channel_wait_operation_pending(remedy_channel_token_t token, uint32_t operation_kind, uint64_t instance_id, uint32_t timeout_ms) {
        std::unique_lock<std::mutex> lock(g_test_seam_mutex);
        return g_test_seam_cv.wait_for(lock, std::chrono::milliseconds(timeout_ms), [token, operation_kind, instance_id]() {
            auto it = g_test_op_pending_map.find({token, operation_kind, instance_id});
            return (it != g_test_op_pending_map.end() && it->second);
        });
    }

    bool remedy_test_channel_record_history_snapshot(const channel_operation_history_snapshot* snapshot) {
        if (!snapshot) return false;
        std::lock_guard<std::mutex> lock(g_test_seam_mutex);
        remedy_test_channel_operation_history hist{};
        hist.found = true; hist.created = snapshot->created; hist.io_submitted = snapshot->io_submitted;
        hist.io_was_pending = snapshot->io_was_pending; hist.public_call_completed = snapshot->public_call_completed;
        hist.kernel_completion_observed = snapshot->kernel_completion_observed; hist.detached_at_public_return = snapshot->detached_at_public_return;
        hist.detached_recovered = snapshot->detached_recovered; hist.no_kernel_request_outstanding_at_public_return = snapshot->no_kernel_request_outstanding_at_public_return;
        hist.final_public_result = snapshot->final_public_result; hist.final_completion_error = snapshot->final_completion_error;
        g_test_history_store[{snapshot->token, snapshot->kind, snapshot->instance_id}] = hist;
        return true;
    }

    bool remedy_test_channel_get_operation_history(remedy_channel_token_t token, uint32_t operation_kind, uint64_t instance_id, remedy_test_channel_operation_history* out_history) {
        if (!out_history) return false;
        std::lock_guard<std::mutex> lock(g_test_seam_mutex);
        auto it = g_test_history_store.find({token, operation_kind, instance_id});
        if (it == g_test_history_store.end()) return false;
        *out_history = it->second;
        return true;
    }

    void remedy_test_channel_increment_buffer_allocated(void) { std::lock_guard<std::mutex> l(g_test_seam_mutex); g_monotonic_counts.buffers_allocated++; }
    void remedy_test_channel_increment_buffer_freed(void) { std::lock_guard<std::mutex> l(g_test_seam_mutex); g_monotonic_counts.buffers_freed++; }
    void remedy_test_channel_increment_detached_created(void) { std::lock_guard<std::mutex> l(g_test_seam_mutex); g_monotonic_counts.detached_created++; }
    void remedy_test_channel_increment_detached_recovered(void) { std::lock_guard<std::mutex> l(g_test_seam_mutex); g_monotonic_counts.detached_recovered++; }
    void remedy_test_channel_increment_finalizer_invocations(void) { std::lock_guard<std::mutex> l(g_test_seam_mutex); g_monotonic_counts.finalizer_invocations++; }
    void remedy_test_channel_increment_finalizer_completions(void) { std::lock_guard<std::mutex> l(g_test_seam_mutex); g_monotonic_counts.finalizer_completions++; }
    void remedy_test_channel_increment_successful_finalizers(void) { std::lock_guard<std::mutex> l(g_test_seam_mutex); g_monotonic_counts.successful_finalizers++; }
    void remedy_test_channel_increment_entry_deletions(void) { std::lock_guard<std::mutex> l(g_test_seam_mutex); g_monotonic_counts.entry_deletions++; }
    void remedy_test_channel_increment_pipe_closures(void) { std::lock_guard<std::mutex> l(g_test_seam_mutex); g_monotonic_counts.pipe_closures++; }
    void remedy_test_channel_increment_event_closures(void) { std::lock_guard<std::mutex> l(g_test_seam_mutex); g_monotonic_counts.event_closures++; }

    bool remedy_test_channel_publish_finalizer_owner(remedy_channel_token_t token, uint64_t cookie, uint64_t generation_number, bool active, bool draining, bool completed, remedy_err_t result, bool entry_remains_registered) {
        std::lock_guard<std::mutex> lock(g_test_seam_mutex);
        channel_finalizer_owner_observation incoming{token, generation_number, cookie, active, draining, completed, result, entry_remains_registered};
        auto it = g_test_finalizer_owner_map.find(token);
        if (it == g_test_finalizer_owner_map.end()) {
            g_test_finalizer_owner_map[token] = incoming;
        } else {
            const auto& existing = it->second;
            if (incoming.generation_number > existing.generation_number) {
                g_test_finalizer_owner_map[token] = incoming;
            } else if (incoming.generation_number == existing.generation_number) {
                int existing_rank = existing.public_destroy_completed ? 2 : 1;
                int incoming_rank = incoming.public_destroy_completed ? 2 : 1;
                if (incoming_rank >= existing_rank) {
                    g_test_finalizer_owner_map[token] = incoming;
                }
            }
        }
        g_test_seam_cv.notify_all();
        return true;
    }

    bool remedy_test_channel_get_finalizer_owner_observation(remedy_channel_token_t token, channel_finalizer_owner_observation* out_obs) {
        if (!out_obs) return false;
        std::lock_guard<std::mutex> lock(g_test_seam_mutex);
        auto it = g_test_finalizer_owner_map.find(token);
        if (it == g_test_finalizer_owner_map.end()) return false;
        *out_obs = it->second;
        return true;
    }

    bool remedy_test_channel_publish_competitor_attached(remedy_channel_token_t token, uint64_t generation_number, uint64_t competitor_cookie) {
        if (token == REMEDY_INVALID_CHANNEL_TOKEN || generation_number == 0 || competitor_cookie == 0) {
            return false;
        }
        std::lock_guard<std::mutex> lock(g_test_seam_mutex);
        auto key = std::make_tuple(token, generation_number);
        auto it = g_test_competitor_attached_map.find(key);
        if (it != g_test_competitor_attached_map.end()) {
            return it->second == competitor_cookie;
        }
        g_test_competitor_attached_map.emplace(key, competitor_cookie);
        g_test_seam_cv.notify_all();
        return true;
    }

    bool remedy_test_channel_wait_competitor_attached(remedy_channel_token_t token, uint64_t generation_number, uint32_t timeout_ms, uint64_t* out_competitor_cookie) {
        if (token == REMEDY_INVALID_CHANNEL_TOKEN || generation_number == 0 || !out_competitor_cookie) {
            return false;
        }
        *out_competitor_cookie = 0;
        std::unique_lock<std::mutex> lock(g_test_seam_mutex);
        auto key = std::make_tuple(token, generation_number);
        bool observed = g_test_seam_cv.wait_for(lock, std::chrono::milliseconds(timeout_ms), [&]() {
            return g_test_competitor_attached_map.find(key) != g_test_competitor_attached_map.end();
        });
        if (!observed) {
            return false;
        }
        *out_competitor_cookie = g_test_competitor_attached_map.at(key);
        return true;
    }

    bool remedy_test_channel_get_entry_snapshot(remedy_channel_token_t token, int* out_state, uint32_t* out_leases, uint32_t* out_close_pins, bool* out_finalizer_active, bool* out_in_registry, bool* out_is_pipe_null, uint32_t* out_retained_event_count) {
        if (token == REMEDY_INVALID_CHANNEL_TOKEN) return false;
        int state_val = 0; uint32_t leases_val = 0, close_pins_val = 0, event_count_val = 0;
        bool fin_active_val = false, in_reg_val = false, pipe_null_val = false;

        {
            std::lock_guard<std::mutex> registry_lock(g_channel_mutex);
            auto it = g_channel_table.find(token);
            if (it == g_channel_table.end() || !it->second) return false;
            win32_channel_entry* entry = it->second;

            std::lock_guard<std::mutex> entry_lock(entry->entry_mutex);
            channel_operation_record* slow = entry->retained_events;
            channel_operation_record* fast = entry->retained_events;
            while (fast != nullptr && fast->next != nullptr) {
                slow = slow->next;
                fast = fast->next->next;
                if (slow == fast) return false;
            }

            state_val = static_cast<int>(entry->state);
            leases_val = entry->active_leases;
            close_pins_val = entry->close_pins;
            fin_active_val = entry->finalizer_active;
            in_reg_val = true;
            pipe_null_val = (entry->pipe_handle == NULL || entry->pipe_handle == INVALID_HANDLE_VALUE);

            channel_operation_record* curr = entry->retained_events;
            while (curr) {
                if (curr->event_handle != NULL && curr->event_handle != INVALID_HANDLE_VALUE) {
                    if (event_count_val == UINT32_MAX) {
                        remedy_test_catastrophic_harness_failure("Overflow retained event count in get_entry_snapshot");
                    }
                    event_count_val++;
                }
                curr = curr->next;
            }
        }

        if (out_state) *out_state = state_val;
        if (out_leases) *out_leases = leases_val;
        if (out_close_pins) *out_close_pins = close_pins_val;
        if (out_finalizer_active) *out_finalizer_active = fin_active_val;
        if (out_in_registry) *out_in_registry = in_reg_val;
        if (out_is_pipe_null) *out_is_pipe_null = pipe_null_val;
        if (out_retained_event_count) *out_retained_event_count = event_count_val;
        return true;
    }

    bool remedy_test_channel_get_operation_snapshot(remedy_channel_token_t token, uint32_t operation_kind, uint64_t instance_id, int* out_state, bool* out_io_submitted, bool* out_io_pending, bool* out_completion_observed, bool* out_detached, bool* out_event_owned, size_t* out_owned_buffer_size) {
        if (token == REMEDY_INVALID_CHANNEL_TOKEN || instance_id == 0) return false;
        if (operation_kind != 1 && operation_kind != 2 && operation_kind != 3) return false;

        int state_val = 0; bool submitted_val = false, pending_val = false, observed_val = false, detached_val = false, event_owned_val = false;
        size_t buf_size_val = 0;

        {
            std::lock_guard<std::mutex> registry_lock(g_channel_mutex);
            auto it = g_channel_table.find(token);
            if (it == g_channel_table.end() || !it->second) return false;
            win32_channel_entry* entry = it->second;

            std::lock_guard<std::mutex> entry_lock(entry->entry_mutex);
            channel_operation_record* slow = entry->retained_events;
            channel_operation_record* fast = entry->retained_events;
            while (fast != nullptr && fast->next != nullptr) {
                slow = slow->next;
                fast = fast->next->next;
                if (slow == fast) return false;
            }

            channel_operation_record* found = nullptr;
            channel_operation_record* curr = entry->retained_events;
            while (curr) {
                if (static_cast<uint32_t>(curr->kind) == operation_kind && curr->instance_id == instance_id) {
                    if (found != nullptr) return false;
                    found = curr;
                }
                curr = curr->next;
            }
            if (!found) return false;

            state_val = static_cast<int>(found->state);
            submitted_val = found->current_request_submitted;
            pending_val = found->current_request_pending;
            observed_val = found->current_request_completion_observed;
            detached_val = (found->completion_owner == channel_completion_owner::DETACHED_FINALIZER);
            event_owned_val = (found->event_handle != NULL && found->event_handle != INVALID_HANDLE_VALUE);
            buf_size_val = found->owned_buffer_size;
        }

        if (out_state) *out_state = state_val;
        if (out_io_submitted) *out_io_submitted = submitted_val;
        if (out_io_pending) *out_io_pending = pending_val;
        if (out_completion_observed) *out_completion_observed = observed_val;
        if (out_detached) *out_detached = detached_val;
        if (out_event_owned) *out_event_owned = event_owned_val;
        if (out_owned_buffer_size) *out_owned_buffer_size = buf_size_val;
        return true;
    }

    bool remedy_test_channel_get_final_counts(uint32_t* out_active_entries, uint32_t* out_active_leases, uint32_t* out_active_close_pins, uint32_t* out_retained_records, uint32_t* out_sub_pending, uint32_t* out_det_pending, uint32_t* out_comp_retained, uint32_t* out_buf_alloc, uint32_t* out_buf_freed, uint32_t* out_det_created, uint32_t* out_det_rec, uint32_t* out_unconsumed_inj, uint32_t* out_armed_p, uint32_t* out_pending_ops, uint32_t* out_fin_inv, uint32_t* out_fin_comp, uint32_t* out_succ_fin, uint32_t* out_entry_del, uint32_t* out_pipe_cls, uint32_t* out_event_cls, uint32_t* out_pipe_cls_fail, uint32_t* out_evt_cls_fail, uint32_t* out_pipe_canc_fail, uint32_t* out_op_canc_fail, uint32_t* out_wait_fail) {
        uint32_t act_entries = 0, act_leases = 0, act_close_pins = 0;
        uint32_t ret_records = 0, sub_pending = 0, det_pending = 0, comp_retained = 0, pending_ops = 0;

        {
            std::lock_guard<std::mutex> registry_lock(g_channel_mutex);
            if (g_channel_table.size() > UINT32_MAX) remedy_test_catastrophic_harness_failure("Overflow active registry entries");
            act_entries = static_cast<uint32_t>(g_channel_table.size());

            for (auto& kv : g_channel_table) {
                win32_channel_entry* entry = kv.second;
                if (!entry) remedy_test_catastrophic_harness_failure("Null registry entry in get_final_counts");
                std::lock_guard<std::mutex> entry_lock(entry->entry_mutex);

                if (entry->active_leases > UINT32_MAX - act_leases) remedy_test_catastrophic_harness_failure("Overflow active leases");
                act_leases += entry->active_leases;
                if (entry->close_pins > UINT32_MAX - act_close_pins) remedy_test_catastrophic_harness_failure("Overflow close pins");
                act_close_pins += entry->close_pins;

                channel_operation_record* slow = entry->retained_events;
                channel_operation_record* fast = entry->retained_events;
                while (fast != nullptr && fast->next != nullptr) {
                    slow = slow->next;
                    fast = fast->next->next;
                    if (slow == fast) remedy_test_catastrophic_harness_failure("Cycle detected in retained operations list");
                }

                uint32_t entry_detached_state_count = 0;
                uint32_t entry_reserved_count = 0;
                channel_operation_record* curr = entry->retained_events;
                while (curr) {
                    if (ret_records == UINT32_MAX) remedy_test_catastrophic_harness_failure("Overflow retained records");
                    ret_records++;

                    if (curr->detachment_reserved) {
                        if (entry_reserved_count == UINT32_MAX) remedy_test_catastrophic_harness_failure("Overflow entry reserved count");
                        entry_reserved_count++;
                    }

                    if (curr->state == channel_operation_state::DETACHED_PENDING) {
                        if (curr->completion_owner != channel_completion_owner::DETACHED_FINALIZER) {
                            remedy_test_catastrophic_harness_failure("DETACHED_PENDING state without DETACHED_FINALIZER owner");
                        }
                        if (entry_detached_state_count == UINT32_MAX) remedy_test_catastrophic_harness_failure("Overflow entry detached state count");
                        entry_detached_state_count++;

                        if (det_pending == UINT32_MAX) remedy_test_catastrophic_harness_failure("Overflow det_pending");
                        det_pending++;
                    } else if (curr->completion_owner == channel_completion_owner::DETACHED_FINALIZER) {
                        remedy_test_catastrophic_harness_failure("DETACHED_FINALIZER owner without DETACHED_PENDING state");
                    } else if (curr->state == channel_operation_state::SUBMITTED_PENDING) {
                        if (sub_pending == UINT32_MAX) remedy_test_catastrophic_harness_failure("Overflow sub_pending");
                        sub_pending++;
                    } else if (curr->state == channel_operation_state::COMPLETED || curr->state == channel_operation_state::RETIREMENT_FAILED) {
                        if (comp_retained == UINT32_MAX) remedy_test_catastrophic_harness_failure("Overflow comp_retained");
                        comp_retained++;
                    }

                    if (curr->current_request_pending) {
                        if (pending_ops == UINT32_MAX) remedy_test_catastrophic_harness_failure("Overflow pending_ops");
                        pending_ops++;
                    }

                    curr = curr->next;
                }

                if (entry_detached_state_count != entry->detached_pending_operations || entry_reserved_count != entry->detachment_reservations) {
                    remedy_test_catastrophic_harness_failure("Retained record count mismatch against entry state");
                }
            }
        }

        uint32_t unconsumed_inj = 0, armed_pause_count = 0;
        channel_test_counts snap_counts{};
        {
            std::lock_guard<std::mutex> slock(g_test_seam_mutex);
            if (!get_exact_armed_injection_count_locked(&unconsumed_inj)) {
                remedy_test_catastrophic_harness_failure("Overflow unconsumed injections");
            }
            if (g_lease_pause.armed) armed_pause_count++;
            if (g_sub_pause.armed) armed_pause_count++;
            if (g_fin_pause.armed) armed_pause_count++;
            if (g_close_pause.armed) armed_pause_count++;
            snap_counts = g_monotonic_counts;
        }

        if (out_active_entries) *out_active_entries = act_entries;
        if (out_active_leases) *out_active_leases = act_leases;
        if (out_active_close_pins) *out_active_close_pins = act_close_pins;
        if (out_retained_records) *out_retained_records = ret_records;
        if (out_sub_pending) *out_sub_pending = sub_pending;
        if (out_det_pending) *out_det_pending = det_pending;
        if (out_comp_retained) *out_comp_retained = comp_retained;
        if (out_buf_alloc) *out_buf_alloc = snap_counts.buffers_allocated;
        if (out_buf_freed) *out_buf_freed = snap_counts.buffers_freed;
        if (out_det_created) *out_det_created = snap_counts.detached_created;
        if (out_det_rec) *out_det_rec = snap_counts.detached_recovered;
        if (out_unconsumed_inj) *out_unconsumed_inj = unconsumed_inj;
        if (out_armed_p) *out_armed_p = armed_pause_count;
        if (out_pending_ops) *out_pending_ops = pending_ops;
        if (out_fin_inv) *out_fin_inv = snap_counts.finalizer_invocations;
        if (out_fin_comp) *out_fin_comp = snap_counts.finalizer_completions;
        if (out_succ_fin) *out_succ_fin = snap_counts.successful_finalizers;
        if (out_entry_del) *out_entry_del = snap_counts.entry_deletions;
        if (out_pipe_cls) *out_pipe_cls = snap_counts.pipe_closures;
        if (out_event_cls) *out_event_cls = snap_counts.event_closures;
        if (out_pipe_cls_fail) *out_pipe_cls_fail = snap_counts.pipe_close_failures;
        if (out_evt_cls_fail) *out_evt_cls_fail = snap_counts.event_close_failures;
        if (out_pipe_canc_fail) *out_pipe_canc_fail = snap_counts.pipe_cancel_failures;
        if (out_op_canc_fail) *out_op_canc_fail = snap_counts.operation_cancel_failures;
        if (out_wait_fail) *out_wait_fail = snap_counts.wait_failures;
        return true;
    }

    bool remedy_test_channel_set_next_token(remedy_channel_token_t next_token, remedy_channel_token_t* out_previous) {
        if (out_previous) *out_previous = g_next_channel_token.load(std::memory_order_relaxed);
        g_next_channel_token.store(next_token, std::memory_order_relaxed);
        return true;
    }
}
#endif

#include "remedy/ports/worker_port.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <unordered_map>
#include <mutex>
#include <condition_variable>
#include <new>
#include <cstdlib>
#include <cwchar>
#include <string>
#include <limits>
#include <chrono>

enum class worker_entry_state {
    LIVE,
    CLOSING,
    RETIRED
};

struct win32_worker_entry {
    std::mutex entry_mutex;
    std::condition_variable cv;

    HANDLE process_handle{NULL};
    HANDLE thread_handle{NULL};
    HANDLE job_handle{NULL};
    DWORD  process_id{0};

    worker_entry_state state{worker_entry_state::LIVE};
    uint32_t active_leases{0};
    bool finalizer_active{false};
};

static std::mutex g_worker_mutex;
static std::unordered_map<remedy_worker_token_t, win32_worker_entry*> g_worker_table;
static remedy_worker_token_t g_next_worker_token = 1;

#ifdef REMEDY_TEST_STAGED_FAILURE_SEAM
static int g_test_fail_stage = 0;
static HANDLE g_test_observer_handle = NULL;

extern "C" {
void remedy_test_set_fail_stage(int stage) {
    g_test_fail_stage = stage;
}

HANDLE remedy_test_take_observer_handle(void) {
    HANDLE h = g_test_observer_handle;
    g_test_observer_handle = NULL;
    return h;
}

size_t remedy_test_get_registry_size(void) {
    std::lock_guard<std::mutex> lock(g_worker_mutex);
    return g_worker_table.size();
}
}
#endif

#ifdef REMEDY_TEST_WORKER_RUNDOWN_SEAM
static std::mutex g_seam_mutex;
static std::condition_variable g_seam_cv;
static remedy_worker_token_t g_seam_armed_pause_token = REMEDY_INVALID_WORKER_TOKEN;
static remedy_worker_token_t g_seam_paused_token = REMEDY_INVALID_WORKER_TOKEN;
static bool g_seam_lease_paused = false;
static bool g_seam_lease_release = false;
static remedy_worker_token_t g_seam_finalizer_waiting_token = REMEDY_INVALID_WORKER_TOKEN;
static bool g_seam_finalizer_waiting = false;
static remedy_worker_token_t g_seam_fail_next_process_close_token = REMEDY_INVALID_WORKER_TOKEN;
static uint32_t g_seam_finalizer_completions = 0;
static uint32_t g_seam_entry_deletions = 0;
static uint32_t g_seam_process_closures = 0;
static uint32_t g_seam_thread_closures = 0;

static void remedy_test_check_lease_pause(remedy_worker_token_t token) {
    std::unique_lock<std::mutex> seam_lock(g_seam_mutex);
    if (g_seam_armed_pause_token == token) {
        g_seam_armed_pause_token = REMEDY_INVALID_WORKER_TOKEN;
        g_seam_paused_token = token;
        g_seam_lease_paused = true;
        g_seam_cv.notify_all();
        bool wait_ok = g_seam_cv.wait_for(seam_lock, std::chrono::milliseconds(10000), [&] {
            return g_seam_lease_release;
        });
        if (!wait_ok) {
            std::abort();
        }
        g_seam_paused_token = REMEDY_INVALID_WORKER_TOKEN;
    }
}
#endif

class worker_lease_guard {
public:
    worker_lease_guard() = default;
    explicit worker_lease_guard(win32_worker_entry* entry) : entry_(entry) {}
    ~worker_lease_guard() { release(); }

    worker_lease_guard(const worker_lease_guard&) = delete;
    worker_lease_guard& operator=(const worker_lease_guard&) = delete;
    worker_lease_guard(worker_lease_guard&&) = delete;
    worker_lease_guard& operator=(worker_lease_guard&&) = delete;

    void adopt(win32_worker_entry* entry) {
        if (entry_ != nullptr) {
            std::abort();
        }
        entry_ = entry;
    }

    win32_worker_entry* get() const { return entry_; }

private:
    win32_worker_entry* entry_{nullptr};

    void release() {
        if (entry_) {
            std::lock_guard<std::mutex> lock(entry_->entry_mutex);
            if (entry_->active_leases == 0) {
                std::abort();
            }
            entry_->active_leases--;
            if (entry_->active_leases == 0 && entry_->state == worker_entry_state::CLOSING) {
                entry_->cv.notify_all();
            }
            entry_ = nullptr;
        }
    }
};

static remedy_err_t worker_entry_acquire_lease(remedy_worker_token_t token, worker_lease_guard* out_lease) {
    if (token == REMEDY_INVALID_WORKER_TOKEN || !out_lease) {
        return REMEDY_ERR_INVALID_ARGUMENT;
    }
    if (out_lease->get() != nullptr) {
        std::abort();
    }
    std::lock_guard<std::mutex> lock(g_worker_mutex);
    auto it = g_worker_table.find(token);
    if (it == g_worker_table.end()) {
        return REMEDY_ERR_INVALID_ARGUMENT;
    }
    win32_worker_entry* entry = it->second;
    std::lock_guard<std::mutex> entry_lock(entry->entry_mutex);
    if (entry->state != worker_entry_state::LIVE) {
        return REMEDY_ERR_REVOKING;
    }
    if (entry->active_leases == (std::numeric_limits<uint32_t>::max)()) {
        std::abort();
    }
    entry->active_leases++;
    out_lease->adopt(entry);
    return REMEDY_OK;
}

[[noreturn]] static void remedy_fail_stop_containment_breach(HANDLE& hProcess, HANDLE& hThread, HANDLE& hJob) {
    while (hProcess != NULL) {
        DWORD waitRes = WaitForSingleObject(hProcess, 100);
        if (waitRes == WAIT_OBJECT_0) {
            break;
        }
        if (hJob != NULL) {
            if (CloseHandle(hJob)) {
                hJob = NULL;
            }
        }
        if (hProcess != NULL) {
            BOOL termOk = TerminateProcess(hProcess, 1);
            if (termOk) {
                waitRes = WaitForSingleObject(hProcess, 100);
                if (waitRes == WAIT_OBJECT_0) {
                    break;
                }
            }
        }
        Sleep(10);
    }

    while (hJob != NULL || hThread != NULL || hProcess != NULL) {
        if (hJob != NULL) {
            if (CloseHandle(hJob)) {
                hJob = NULL;
            }
        }
        if (hThread != NULL) {
            if (CloseHandle(hThread)) {
                hThread = NULL;
            }
        }
        if (hProcess != NULL) {
            if (CloseHandle(hProcess)) {
                hProcess = NULL;
            }
        }
        if (hJob != NULL || hThread != NULL || hProcess != NULL) {
            Sleep(10);
        }
    }

    std::abort();
}

static void do_verified_failure_cleanup(HANDLE& hProcess, HANDLE& hThread, HANDLE& hJob, bool assigned_to_job) {
    bool term_requested = false;
    if (assigned_to_job && hJob != NULL) {
        if (CloseHandle(hJob)) {
            hJob = NULL;
            term_requested = true;
        }
    } else {
        if (hProcess != NULL) {
            BOOL termOk = TerminateProcess(hProcess, 1);
            if (termOk) {
                term_requested = true;
            }
        }
    }

    if (hProcess != NULL) {
        DWORD waitRes = WaitForSingleObject(hProcess, 2000);
        if (waitRes != WAIT_OBJECT_0 || !term_requested) {
            if (waitRes != WAIT_OBJECT_0) {
                remedy_fail_stop_containment_breach(hProcess, hThread, hJob);
            }
        }
    }

    if (hThread != NULL) {
        if (CloseHandle(hThread)) {
            hThread = NULL;
        } else {
            remedy_fail_stop_containment_breach(hProcess, hThread, hJob);
        }
    }

    if (hProcess != NULL) {
        if (CloseHandle(hProcess)) {
            hProcess = NULL;
        } else {
            remedy_fail_stop_containment_breach(hProcess, hThread, hJob);
        }
    }

    if (hJob != NULL) {
        if (CloseHandle(hJob)) {
            hJob = NULL;
        } else {
            remedy_fail_stop_containment_breach(hProcess, hThread, hJob);
        }
    }
}

static bool is_valid_absolute_path(const char* path) {
    if (!path || path[0] == '\0') return false;
    if (path[1] == '\0') return false;

    // Drive-rooted: requires at least 3 chars (path[0], path[1], path[2])
    if (((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z')) && path[1] == ':') {
        if (path[2] == '/' || path[2] == '\\') {
            return true;
        }
        return false;
    }

    // UNC or Extended path: requires at least 2 chars (path[0], path[1])
    if (path[0] == '\\' && path[1] == '\\') {
        return true;
    }

    return false;
}

static bool convert_utf8_to_wide(const char* utf8_str, std::wstring& out_wide) {
    if (!utf8_str) return false;
    int req_chars = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8_str, -1, NULL, 0);
    if (req_chars <= 0) return false;

    out_wide.resize(req_chars);
    int converted = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8_str, -1, &out_wide[0], req_chars);
    if (converted != req_chars) return false;
    if (out_wide[req_chars - 1] != L'\0') return false;

    out_wide.resize(req_chars - 1);
    return true;
}

static remedy_worker_token_t generate_unique_token_locked() {
    remedy_worker_token_t start_token = g_next_worker_token;
    do {
        remedy_worker_token_t token = g_next_worker_token++;
        if (token == REMEDY_INVALID_WORKER_TOKEN) {
            token = g_next_worker_token++;
        }
        if (g_worker_table.find(token) == g_worker_table.end()) {
            return token;
        }
    } while (g_next_worker_token != start_token);
    return REMEDY_INVALID_WORKER_TOKEN;
}

extern "C" {

remedy_err_t worker_port_start(const remedy_worker_config_t* config, remedy_worker_token_t* out_token) {
    if (!out_token) return REMEDY_ERR_INVALID_ARGUMENT;
    *out_token = REMEDY_INVALID_WORKER_TOKEN;

    if (!config) return REMEDY_ERR_INVALID_ARGUMENT;
    if (!config->executable_path || config->executable_path[0] == '\0') return REMEDY_ERR_INVALID_ARGUMENT;

    if (config->arguments && config->arguments[0] != '\0') return REMEDY_ERR_NOT_SUPPORTED;
    if (config->channel_nonce && config->channel_nonce[0] != '\0') return REMEDY_ERR_NOT_SUPPORTED;
    if (config->timeout_ms != 0) return REMEDY_ERR_NOT_SUPPORTED;

    if (!is_valid_absolute_path(config->executable_path)) return REMEDY_ERR_INVALID_ARGUMENT;

    std::wstring wExecPath;
    std::wstring wWorkDir;
    const wchar_t* pWorkDir = NULL;

    try {
        if (!convert_utf8_to_wide(config->executable_path, wExecPath)) {
            return REMEDY_ERR_INVALID_ARGUMENT;
        }

        if (config->working_directory && config->working_directory[0] != '\0') {
            if (!convert_utf8_to_wide(config->working_directory, wWorkDir)) {
                return REMEDY_ERR_INVALID_ARGUMENT;
            }
            pWorkDir = wWorkDir.c_str();
        }
    } catch (const std::bad_alloc&) {
        return REMEDY_ERR_OUT_OF_MEMORY;
    }

    STARTUPINFOW si = { sizeof(si) };
    PROCESS_INFORMATION pi = { 0 };

    BOOL procSuccess = CreateProcessW(
        wExecPath.c_str(),
        NULL,
        NULL, NULL, FALSE,
        CREATE_SUSPENDED,
        NULL, pWorkDir, &si, &pi
    );

    if (!procSuccess) return REMEDY_ERR_IPC_FAILURE;

#ifdef REMEDY_TEST_STAGED_FAILURE_SEAM
    if (g_test_fail_stage >= 1 && g_test_fail_stage <= 7) {
        BOOL dupOk = DuplicateHandle(
            GetCurrentProcess(), pi.hProcess,
            GetCurrentProcess(), &g_test_observer_handle,
            0, FALSE, DUPLICATE_SAME_ACCESS
        );
        if (!dupOk) {
            HANDLE hJob = NULL;
            do_verified_failure_cleanup(pi.hProcess, pi.hThread, hJob, false);
            return REMEDY_ERR_CONTAINMENT_FAILED;
        }
    }
    if (g_test_fail_stage == 1) {
        HANDLE hJob = NULL;
        do_verified_failure_cleanup(pi.hProcess, pi.hThread, hJob, false);
        return REMEDY_ERR_CONTAINMENT_FAILED;
    }
#endif

    HANDLE hJob = CreateJobObjectW(NULL, NULL);
    if (!hJob) {
        do_verified_failure_cleanup(pi.hProcess, pi.hThread, hJob, false);
        return REMEDY_ERR_CONTAINMENT_FAILED;
    }

#ifdef REMEDY_TEST_STAGED_FAILURE_SEAM
    if (g_test_fail_stage == 2) {
        do_verified_failure_cleanup(pi.hProcess, pi.hThread, hJob, false);
        return REMEDY_ERR_CONTAINMENT_FAILED;
    }
#endif

    JOBOBJECT_EXTENDED_LIMIT_INFORMATION jeli = { 0 };
    jeli.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!SetInformationJobObject(hJob, JobObjectExtendedLimitInformation, &jeli, sizeof(jeli))) {
        do_verified_failure_cleanup(pi.hProcess, pi.hThread, hJob, false);
        return REMEDY_ERR_CONTAINMENT_FAILED;
    }

#ifdef REMEDY_TEST_STAGED_FAILURE_SEAM
    if (g_test_fail_stage == 3) {
        do_verified_failure_cleanup(pi.hProcess, pi.hThread, hJob, false);
        return REMEDY_ERR_CONTAINMENT_FAILED;
    }
#endif

    if (!AssignProcessToJobObject(hJob, pi.hProcess)) {
        do_verified_failure_cleanup(pi.hProcess, pi.hThread, hJob, false);
        return REMEDY_ERR_CONTAINMENT_FAILED;
    }

#ifdef REMEDY_TEST_STAGED_FAILURE_SEAM
    if (g_test_fail_stage == 4) {
        do_verified_failure_cleanup(pi.hProcess, pi.hThread, hJob, true);
        return REMEDY_ERR_CONTAINMENT_FAILED;
    }
#endif

    if (ResumeThread(pi.hThread) == (DWORD)-1) {
        do_verified_failure_cleanup(pi.hProcess, pi.hThread, hJob, true);
        return REMEDY_ERR_CONTAINMENT_FAILED;
    }

#ifdef REMEDY_TEST_STAGED_FAILURE_SEAM
    if (g_test_fail_stage == 5) {
        do_verified_failure_cleanup(pi.hProcess, pi.hThread, hJob, true);
        return REMEDY_ERR_CONTAINMENT_FAILED;
    }
#endif

    win32_worker_entry* entry = new (std::nothrow) win32_worker_entry();
    if (!entry) {
        do_verified_failure_cleanup(pi.hProcess, pi.hThread, hJob, true);
        return REMEDY_ERR_OUT_OF_MEMORY;
    }

#ifdef REMEDY_TEST_STAGED_FAILURE_SEAM
    if (g_test_fail_stage == 6) {
        delete entry;
        do_verified_failure_cleanup(pi.hProcess, pi.hThread, hJob, true);
        return REMEDY_ERR_OUT_OF_MEMORY;
    }
#endif

    entry->process_handle = pi.hProcess;
    entry->thread_handle = pi.hThread;
    entry->job_handle = hJob;
    entry->process_id = pi.dwProcessId;

    remedy_worker_token_t token = REMEDY_INVALID_WORKER_TOKEN;
    try {
        std::lock_guard<std::mutex> lock(g_worker_mutex);
        token = generate_unique_token_locked();
        if (token == REMEDY_INVALID_WORKER_TOKEN) {
            delete entry;
            do_verified_failure_cleanup(pi.hProcess, pi.hThread, hJob, true);
            return REMEDY_ERR_OUT_OF_MEMORY;
        }
        g_worker_table[token] = entry;
    } catch (const std::bad_alloc&) {
        delete entry;
        do_verified_failure_cleanup(pi.hProcess, pi.hThread, hJob, true);
        return REMEDY_ERR_OUT_OF_MEMORY;
    }

#ifdef REMEDY_TEST_STAGED_FAILURE_SEAM
    if (g_test_fail_stage == 7) {
        {
            std::lock_guard<std::mutex> lock(g_worker_mutex);
            g_worker_table.erase(token);
        }
        do_verified_failure_cleanup(entry->process_handle, entry->thread_handle, entry->job_handle, true);
        if (entry->process_handle != NULL || entry->thread_handle != NULL || entry->job_handle != NULL) {
            remedy_fail_stop_containment_breach(entry->process_handle, entry->thread_handle, entry->job_handle);
        }
        delete entry;
        return REMEDY_ERR_CONTAINMENT_FAILED;
    }
#endif

    *out_token = token;
    return REMEDY_OK;
}

remedy_err_t worker_port_request_quiescence(remedy_worker_token_t token) {
    return REMEDY_ERR_NOT_SUPPORTED;
}

remedy_err_t worker_port_terminate(remedy_worker_token_t token) {
    worker_lease_guard lease;
    remedy_err_t err = worker_entry_acquire_lease(token, &lease);
    if (err != REMEDY_OK) return err;

    win32_worker_entry* entry = lease.get();
    std::lock_guard<std::mutex> lock(entry->entry_mutex);
    if (entry->job_handle == NULL) {
        return REMEDY_OK;
    }
    if (CloseHandle(entry->job_handle)) {
        entry->job_handle = NULL;
        return REMEDY_OK;
    } else {
        return REMEDY_ERR_CONTAINMENT_FAILED;
    }
}

remedy_err_t worker_port_wait_for_death(remedy_worker_token_t token, uint32_t timeout_ms, bool* out_died) {
    if (!out_died) return REMEDY_ERR_INVALID_ARGUMENT;
    *out_died = false;

    worker_lease_guard lease;
    remedy_err_t err = worker_entry_acquire_lease(token, &lease);
    if (err != REMEDY_OK) return err;

    win32_worker_entry* entry = lease.get();
    HANDLE hProc = NULL;
    {
        std::lock_guard<std::mutex> lock(entry->entry_mutex);
        if (!entry->process_handle) return REMEDY_ERR_INVALID_ARGUMENT;
        hProc = entry->process_handle;
    }

#ifdef REMEDY_TEST_WORKER_RUNDOWN_SEAM
    remedy_test_check_lease_pause(token);
#endif

    DWORD res = WaitForSingleObject(hProc, timeout_ms);
    if (res == WAIT_OBJECT_0) {
        *out_died = true;
        return REMEDY_OK;
    } else if (res == WAIT_TIMEOUT) {
        *out_died = false;
        return REMEDY_ERR_TIMEOUT;
    } else {
        *out_died = false;
        return REMEDY_ERR_WAIT_FAILED;
    }
}

remedy_err_t worker_port_destroy(remedy_worker_token_t token) {
    if (token == REMEDY_INVALID_WORKER_TOKEN) return REMEDY_ERR_INVALID_ARGUMENT;

    win32_worker_entry* entry = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_worker_mutex);
        auto it = g_worker_table.find(token);
        if (it == g_worker_table.end()) return REMEDY_ERR_INVALID_ARGUMENT;
        entry = it->second;

        std::lock_guard<std::mutex> entry_lock(entry->entry_mutex);

        if (entry->state == worker_entry_state::CLOSING && entry->finalizer_active) {
            return REMEDY_ERR_REVOKING;
        }

        if (entry->state != worker_entry_state::LIVE && entry->state != worker_entry_state::CLOSING) {
            std::abort();
        }

        if (entry->job_handle != NULL) return REMEDY_ERR_INVALID_ARGUMENT;

        if (entry->process_handle == NULL) return REMEDY_ERR_INVALID_ARGUMENT;
        DWORD waitRes = WaitForSingleObject(entry->process_handle, 0);
        if (waitRes == WAIT_TIMEOUT) {
            return REMEDY_ERR_INVALID_ARGUMENT;
        }
        if (waitRes == WAIT_FAILED) {
            return REMEDY_ERR_WAIT_FAILED;
        }
        if (waitRes != WAIT_OBJECT_0) {
            return REMEDY_ERR_WAIT_FAILED;
        }

        if (entry->state == worker_entry_state::LIVE) {
            entry->state = worker_entry_state::CLOSING;
        }

        entry->finalizer_active = true;
    }

    std::unique_lock<std::mutex> entry_lock(entry->entry_mutex);

#ifdef REMEDY_TEST_WORKER_RUNDOWN_SEAM
    if (entry->active_leases > 0) {
        std::lock_guard<std::mutex> seam_lock(g_seam_mutex);
        g_seam_finalizer_waiting_token = token;
        g_seam_finalizer_waiting = true;
        g_seam_cv.notify_all();
    }
#endif

    if (entry->active_leases > 0) {
        bool drained = entry->cv.wait_for(entry_lock, std::chrono::milliseconds(2000), [&] {
            return entry->active_leases == 0;
        });

        if (!drained) {
            entry->finalizer_active = false;
            entry->cv.notify_all();
            return REMEDY_ERR_TIMEOUT;
        }
    }

    if (entry->thread_handle != NULL) {
        if (CloseHandle(entry->thread_handle)) {
            entry->thread_handle = NULL;
#ifdef REMEDY_TEST_WORKER_RUNDOWN_SEAM
            {
                std::lock_guard<std::mutex> seam_lock(g_seam_mutex);
                g_seam_thread_closures++;
            }
#endif
        } else {
            entry->finalizer_active = false;
            entry->cv.notify_all();
            return REMEDY_ERR_CONTAINMENT_FAILED;
        }
    }

    if (entry->process_handle != NULL) {
        bool fail_proc_close = false;
#ifdef REMEDY_TEST_WORKER_RUNDOWN_SEAM
        {
            std::lock_guard<std::mutex> seam_lock(g_seam_mutex);
            if (g_seam_fail_next_process_close_token == token) {
                g_seam_fail_next_process_close_token = REMEDY_INVALID_WORKER_TOKEN;
                fail_proc_close = true;
            }
        }
#endif
        if (fail_proc_close || !CloseHandle(entry->process_handle)) {
            entry->finalizer_active = false;
            entry->cv.notify_all();
            return REMEDY_ERR_CONTAINMENT_FAILED;
        } else {
            entry->process_handle = NULL;
#ifdef REMEDY_TEST_WORKER_RUNDOWN_SEAM
            {
                std::lock_guard<std::mutex> seam_lock(g_seam_mutex);
                g_seam_process_closures++;
            }
#endif
        }
    }

    entry_lock.unlock();

    {
        std::lock_guard<std::mutex> lock(g_worker_mutex);
        std::lock_guard<std::mutex> elock(entry->entry_mutex);

        auto it = g_worker_table.find(token);
        if (it == g_worker_table.end() || it->second != entry ||
            entry->state != worker_entry_state::CLOSING ||
            !entry->finalizer_active ||
            entry->active_leases != 0 ||
            entry->job_handle != NULL ||
            entry->thread_handle != NULL ||
            entry->process_handle != NULL) {
            std::abort();
        }

        entry->state = worker_entry_state::RETIRED;
        g_worker_table.erase(it);
        entry->finalizer_active = false;

#ifdef REMEDY_TEST_WORKER_RUNDOWN_SEAM
        {
            std::lock_guard<std::mutex> seam_lock(g_seam_mutex);
            g_seam_finalizer_completions++;
        }
#endif
    }

#ifdef REMEDY_TEST_WORKER_RUNDOWN_SEAM
    {
        std::lock_guard<std::mutex> seam_lock(g_seam_mutex);
        g_seam_entry_deletions++;
    }
#endif

    delete entry;
    return REMEDY_OK;
}

} // extern "C"

#ifdef REMEDY_TEST_WORKER_RUNDOWN_SEAM
extern "C" {

void remedy_test_reset_seam(void) {
    std::lock_guard<std::mutex> seam_lock(g_seam_mutex);
    g_seam_armed_pause_token = REMEDY_INVALID_WORKER_TOKEN;
    g_seam_paused_token = REMEDY_INVALID_WORKER_TOKEN;
    g_seam_lease_paused = false;
    g_seam_lease_release = false;
    g_seam_finalizer_waiting_token = REMEDY_INVALID_WORKER_TOKEN;
    g_seam_finalizer_waiting = false;
    g_seam_fail_next_process_close_token = REMEDY_INVALID_WORKER_TOKEN;
    g_seam_finalizer_completions = 0;
    g_seam_entry_deletions = 0;
    g_seam_process_closures = 0;
    g_seam_thread_closures = 0;
}

void remedy_test_arm_lease_pause(remedy_worker_token_t token) {
    std::lock_guard<std::mutex> seam_lock(g_seam_mutex);
    g_seam_armed_pause_token = token;
    g_seam_paused_token = REMEDY_INVALID_WORKER_TOKEN;
    g_seam_lease_paused = false;
    g_seam_lease_release = false;
}

bool remedy_test_wait_lease_paused(remedy_worker_token_t token, uint32_t timeout_ms) {
    std::unique_lock<std::mutex> seam_lock(g_seam_mutex);
    return g_seam_cv.wait_for(seam_lock, std::chrono::milliseconds(timeout_ms), [&] {
        return g_seam_lease_paused && (g_seam_paused_token == token);
    });
}

void remedy_test_release_lease_pause(void) {
    std::lock_guard<std::mutex> seam_lock(g_seam_mutex);
    g_seam_lease_release = true;
    g_seam_cv.notify_all();
}

void remedy_test_fail_next_process_handle_close(remedy_worker_token_t token) {
    std::lock_guard<std::mutex> seam_lock(g_seam_mutex);
    g_seam_fail_next_process_close_token = token;
}

bool remedy_test_wait_finalizer_waiting(remedy_worker_token_t token, uint32_t timeout_ms) {
    std::unique_lock<std::mutex> seam_lock(g_seam_mutex);
    bool ok = g_seam_cv.wait_for(seam_lock, std::chrono::milliseconds(timeout_ms), [&] {
        return g_seam_finalizer_waiting && (g_seam_finalizer_waiting_token == token);
    });
    if (ok) {
        g_seam_finalizer_waiting = false;
        g_seam_finalizer_waiting_token = REMEDY_INVALID_WORKER_TOKEN;
    }
    return ok;
}

bool remedy_test_get_entry_snapshot(
    remedy_worker_token_t token,
    int* out_state,
    uint32_t* out_leases,
    bool* out_finalizer_active,
    bool* out_in_registry,
    bool* out_is_job_null,
    bool* out_is_thread_null,
    bool* out_is_process_null
) {
    if (!out_state || !out_leases || !out_finalizer_active || !out_in_registry ||
        !out_is_job_null || !out_is_thread_null || !out_is_process_null) {
        return false;
    }

    *out_state = 0;
    *out_leases = 0;
    *out_finalizer_active = false;
    *out_in_registry = false;
    *out_is_job_null = true;
    *out_is_thread_null = true;
    *out_is_process_null = true;

    std::lock_guard<std::mutex> lock(g_worker_mutex);
    auto it = g_worker_table.find(token);
    if (it == g_worker_table.end()) {
        *out_in_registry = false;
        return false;
    }

    win32_worker_entry* entry = it->second;
    std::lock_guard<std::mutex> entry_lock(entry->entry_mutex);

    *out_in_registry = true;
    *out_state = static_cast<int>(entry->state);
    *out_leases = entry->active_leases;
    *out_finalizer_active = entry->finalizer_active;
    *out_is_job_null = (entry->job_handle == NULL);
    *out_is_thread_null = (entry->thread_handle == NULL);
    *out_is_process_null = (entry->process_handle == NULL);

    return true;
}

void remedy_test_get_final_counts(
    uint32_t* out_finalizer_completions,
    uint32_t* out_entry_deletions,
    uint32_t* out_process_closures,
    uint32_t* out_thread_closures
) {
    if (!out_finalizer_completions || !out_entry_deletions ||
        !out_process_closures || !out_thread_closures) {
        return;
    }

    *out_finalizer_completions = 0;
    *out_entry_deletions = 0;
    *out_process_closures = 0;
    *out_thread_closures = 0;

    std::lock_guard<std::mutex> seam_lock(g_seam_mutex);
    *out_finalizer_completions = g_seam_finalizer_completions;
    *out_entry_deletions = g_seam_entry_deletions;
    *out_process_closures = g_seam_process_closures;
    *out_thread_closures = g_seam_thread_closures;
}

} // extern "C"
#endif

#ifdef REMEDY_TEST_WORKER_TREE_SEAM
extern "C" {

bool remedy_test_worker_job_get_policy(
    remedy_worker_token_t token,
    uint32_t* out_limit_flags,
    uint32_t* out_active_processes
) {
    if (out_limit_flags) *out_limit_flags = 0;
    if (out_active_processes) *out_active_processes = 0;
    if (!out_limit_flags || !out_active_processes) return false;

    std::lock_guard<std::mutex> lock(g_worker_mutex);
    auto it = g_worker_table.find(token);
    if (it == g_worker_table.end()) return false;

    win32_worker_entry* entry = it->second;
    std::lock_guard<std::mutex> elock(entry->entry_mutex);

    if (entry->state != worker_entry_state::LIVE || entry->job_handle == NULL) return false;

    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    if (!QueryInformationJobObject(entry->job_handle, JobObjectExtendedLimitInformation, &limits, sizeof(limits), nullptr)) {
        return false;
    }

    JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting{};
    if (!QueryInformationJobObject(entry->job_handle, JobObjectBasicAccountingInformation, &accounting, sizeof(accounting), nullptr)) {
        return false;
    }

    *out_limit_flags = limits.BasicLimitInformation.LimitFlags;
    *out_active_processes = accounting.ActiveProcesses;
    return true;
}

bool remedy_test_worker_job_contains_pid(
    remedy_worker_token_t token,
    uint32_t pid,
    bool* out_contains
) {
    if (out_contains) *out_contains = false;
    if (!out_contains || pid == 0) return false;

    std::lock_guard<std::mutex> lock(g_worker_mutex);
    auto it = g_worker_table.find(token);
    if (it == g_worker_table.end()) return false;

    win32_worker_entry* entry = it->second;
    std::lock_guard<std::mutex> elock(entry->entry_mutex);

    if (entry->state != worker_entry_state::LIVE || entry->job_handle == NULL) return false;

    constexpr DWORD kMaxPids = 64;
    constexpr size_t kBufferSize = offsetof(JOBOBJECT_BASIC_PROCESS_ID_LIST, ProcessIdList) + kMaxPids * sizeof(ULONG_PTR);
    static_assert(kBufferSize <= (std::numeric_limits<DWORD>::max)(), "Buffer size exceeds DWORD limit");

    alignas(JOBOBJECT_BASIC_PROCESS_ID_LIST) uint8_t rawBuffer[kBufferSize]{};
    PJOBOBJECT_BASIC_PROCESS_ID_LIST pList = reinterpret_cast<PJOBOBJECT_BASIC_PROCESS_ID_LIST>(rawBuffer);

    if (!QueryInformationJobObject(entry->job_handle, JobObjectBasicProcessIdList, pList, static_cast<DWORD>(kBufferSize), nullptr)) {
        return false;
    }

    if (pList->NumberOfAssignedProcesses > kMaxPids ||
        pList->NumberOfProcessIdsInList > kMaxPids ||
        pList->NumberOfProcessIdsInList != pList->NumberOfAssignedProcesses) {
        return false;
    }

    bool found = false;
    for (DWORD i = 0; i < pList->NumberOfProcessIdsInList; ++i) {
        if (pList->ProcessIdList[i] == static_cast<ULONG_PTR>(pid)) {
            found = true;
            break;
        }
    }

    *out_contains = found;
    return true;
}

} // extern "C"
#endif

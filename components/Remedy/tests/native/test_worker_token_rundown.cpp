#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include "remedy/ports/worker_port.h"
#include <stdio.h>
#include <stdlib.h>
#include <assert.h>
#include <chrono>
#include <thread>
#include <mutex>
#include <condition_variable>
#include <string>
#include <filesystem>
#include <cstdint>

#ifdef REMEDY_TEST_WORKER_RUNDOWN_SEAM
extern "C" {
    void remedy_test_reset_seam(void);
    void remedy_test_arm_lease_pause(remedy_worker_token_t token);
    bool remedy_test_wait_lease_paused(remedy_worker_token_t token, uint32_t timeout_ms);
    void remedy_test_release_lease_pause(void);
    void remedy_test_fail_next_process_handle_close(remedy_worker_token_t token);
    bool remedy_test_wait_finalizer_waiting(remedy_worker_token_t token, uint32_t timeout_ms);
    bool remedy_test_get_entry_snapshot(
        remedy_worker_token_t token,
        int* out_state,
        uint32_t* out_leases,
        bool* out_finalizer_active,
        bool* out_in_registry,
        bool* out_is_job_null,
        bool* out_is_thread_null,
        bool* out_is_process_null
    );
    void remedy_test_get_final_counts(
        uint32_t* out_finalizer_completions,
        uint32_t* out_entry_deletions,
        uint32_t* out_process_closures,
        uint32_t* out_thread_closures
    );
}
#endif

namespace fs = std::filesystem;

static void create_dir_clean(const fs::path& p) {
    if (fs::exists(p)) {
        fs::remove_all(p);
    }
    fs::create_directories(p);
}

struct test_thread_result {
    std::mutex mutex;
    std::condition_variable cv;
    bool completed{false};
    remedy_err_t err{REMEDY_OK};
    bool died_flag{false};
    int64_t elapsed_ms{0};

    void signal(remedy_err_t res, bool died = false, int64_t elapsed = 0) {
        std::lock_guard<std::mutex> lock(mutex);
        err = res;
        died_flag = died;
        elapsed_ms = elapsed;
        completed = true;
        cv.notify_all();
    }

    bool wait_completion(uint32_t timeout_ms) {
        std::unique_lock<std::mutex> lock(mutex);
        return cv.wait_for(lock, std::chrono::milliseconds(timeout_ms), [&]{ return completed; });
    }
};

int main(int argc, char* argv[]) {
    if (argc < 2) {
        fprintf(stderr, "Usage: %s <path_to_remedy_worker_fixture.exe>\n", argv[0]);
        return 1;
    }

    const char* fixture_exe = argv[1];

#ifdef REMEDY_TEST_WORKER_RUNDOWN_SEAM
    remedy_test_reset_seam();
#endif

    // Step 1 & 2: Win32 raw process creation warm-up & baseline handle count
    {
        STARTUPINFOW si = { sizeof(si) };
        PROCESS_INFORMATION pi = { 0 };
        wchar_t wExec[1024] = { 0 };
        int req = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, fixture_exe, -1, wExec, 1024);
        assert(req > 0);
        BOOL pOk = CreateProcessW(wExec, NULL, NULL, NULL, FALSE, CREATE_SUSPENDED, NULL, NULL, &si, &pi);
        assert(pOk);
        BOOL tOk = TerminateProcess(pi.hProcess, 1);
        assert(tOk);
        DWORD wRes = WaitForSingleObject(pi.hProcess, 2000);
        assert(wRes == WAIT_OBJECT_0);
        BOOL c1 = CloseHandle(pi.hThread);
        assert(c1);
        BOOL c2 = CloseHandle(pi.hProcess);
        assert(c2);
    }

    DWORD baseline_handles = 0;
    BOOL ghc_ok = GetProcessHandleCount(GetCurrentProcess(), &baseline_handles);
    assert(ghc_ok);

    fs::path test_dir = fs::temp_directory_path() / (
        "remedy_test_rundown_p" + std::to_string(GetCurrentProcessId())
    );
    create_dir_clean(test_dir);
    std::string test_dir_str = test_dir.string();
    fs::path marker_path = test_dir / "remedy_fixture_marker.tmp";

    assert(!fs::exists(marker_path));

    // Step 3: Start worker
    remedy_worker_token_t token = REMEDY_INVALID_WORKER_TOKEN;
    remedy_worker_config_t config = {0};
    config.executable_path = fixture_exe;
    config.working_directory = test_dir_str.c_str();

    remedy_err_t err = worker_port_start(&config, &token);
    assert(err == REMEDY_OK);
    assert(token != REMEDY_INVALID_WORKER_TOKEN);

    // Step 4: Observe fresh marker file
    auto start_time = std::chrono::steady_clock::now();
    bool marker_found = false;
    while (std::chrono::steady_clock::now() - start_time < std::chrono::milliseconds(2000)) {
        if (fs::exists(marker_path)) {
            marker_found = true;
            break;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    assert(marker_found);

    // Step 5: Terminate worker through Job Object closure
    err = worker_port_terminate(token);
    assert(err == REMEDY_OK);

    // Step 6: Observe confirmed process death
    bool died = false;
    err = worker_port_wait_for_death(token, 2000, &died);
    assert(err == REMEDY_OK);
    assert(died);

#ifdef REMEDY_TEST_WORKER_RUNDOWN_SEAM
    // Step 7: Arm lease pause seam
    remedy_test_arm_lease_pause(token);

    // Step 8: Launch operation thread calling worker_port_wait_for_death
    test_thread_result op_res;
    std::thread op_thread([&]() {
        bool op_died = false;
        remedy_err_t res = worker_port_wait_for_death(token, 10000, &op_died);
        op_res.signal(res, op_died);
    });

    // Step 9 & 10: Wait until lease is paused and assert snapshot
    bool pause_ok = remedy_test_wait_lease_paused(token, 5000);
    assert(pause_ok);

    int state = -1;
    uint32_t leases = 0;
    bool finalizer_active = true;
    bool in_registry = false;
    bool is_job_null = false;
    bool is_thread_null = false;
    bool is_process_null = false;

    bool snap_ok = remedy_test_get_entry_snapshot(
        token, &state, &leases, &finalizer_active, &in_registry,
        &is_job_null, &is_thread_null, &is_process_null
    );
    assert(snap_ok);
    assert(in_registry == true);
    assert(state == 0); // LIVE
    assert(leases == 1);
    assert(!finalizer_active);
    assert(is_job_null);
    assert(!is_thread_null);
    assert(!is_process_null);

    // Step 11 & 12: Launch destroy caller thread A measuring elapsed duration inside thread A
    test_thread_result destroyA_res;
    std::thread destroyA_thread([&]() {
        auto t0 = std::chrono::steady_clock::now();
        remedy_err_t res = worker_port_destroy(token);
        auto t1 = std::chrono::steady_clock::now();
        auto dur = std::chrono::duration_cast<std::chrono::milliseconds>(t1 - t0).count();
        destroyA_res.signal(res, false, dur);
    });

    bool waitA_ok = remedy_test_wait_finalizer_waiting(token, 5000);
    assert(waitA_ok); // Consumes A's notification

    // Step 13 & 14: Invoke competing destroy caller B on main thread
    remedy_err_t errB = worker_port_destroy(token);
    assert(errB == REMEDY_ERR_REVOKING);

    // Step 15 & 16: Keep lease paused until thread A times out (~2000ms)
    bool doneA = destroyA_res.wait_completion(5000);
    assert(doneA);
    assert(destroyA_res.err == REMEDY_ERR_TIMEOUT);
    destroyA_thread.join();

    assert(destroyA_res.elapsed_ms >= 1800 && destroyA_res.elapsed_ms <= 4500);

    // Step 17: Assert CLOSING snapshot after destroy A timeout
    snap_ok = remedy_test_get_entry_snapshot(
        token, &state, &leases, &finalizer_active, &in_registry,
        &is_job_null, &is_thread_null, &is_process_null
    );
    assert(snap_ok);
    assert(in_registry == true);
    assert(state == 1); // CLOSING
    assert(leases == 1);
    assert(!finalizer_active);

    // Step 18: Verify operations on CLOSING token return REMEDY_ERR_REVOKING and reset out_died
    err = worker_port_terminate(token);
    assert(err == REMEDY_ERR_REVOKING);

    bool test_died = true;
    err = worker_port_wait_for_death(token, 100, &test_died);
    assert(err == REMEDY_ERR_REVOKING);
    assert(!test_died);

    // Step 19 & 20: Launch destroy retry thread C and wait until C is waiting
    test_thread_result destroyC_res;
    std::thread destroyC_thread([&]() {
        remedy_err_t res = worker_port_destroy(token);
        destroyC_res.signal(res);
    });

    bool waitC_ok = remedy_test_wait_finalizer_waiting(token, 5000);
    assert(waitC_ok); // Proves C claimed finalizer ownership and entered lease drain

    // Step 21: Arm one-shot process-handle-close failure BEFORE releasing pause
    remedy_test_fail_next_process_handle_close(token);

    // Step 22 & 23: Release lease pause
    remedy_test_release_lease_pause();

    bool done_op = op_res.wait_completion(5000);
    assert(done_op);
    assert(op_res.err == REMEDY_OK);
    assert(op_res.died_flag);
    op_thread.join();

    // Step 24: Destroy C wakes, attempts finalization, process close fails -> REMEDY_ERR_CONTAINMENT_FAILED
    bool doneC = destroyC_res.wait_completion(5000);
    assert(doneC);
    assert(destroyC_res.err == REMEDY_ERR_CONTAINMENT_FAILED);
    destroyC_thread.join();

    // Step 25 & 26: Assert partial-finalization snapshot and counters
    snap_ok = remedy_test_get_entry_snapshot(
        token, &state, &leases, &finalizer_active, &in_registry,
        &is_job_null, &is_thread_null, &is_process_null
    );
    assert(snap_ok);
    assert(in_registry == true);
    assert(state == 1); // CLOSING
    assert(leases == 0);
    assert(!finalizer_active);
    assert(is_thread_null == true);
    assert(is_process_null == false);

    uint32_t finalizer_completions = 0;
    uint32_t entry_deletions = 0;
    uint32_t process_closures = 0;
    uint32_t thread_closures = 0;

    remedy_test_get_final_counts(&finalizer_completions, &entry_deletions, &process_closures, &thread_closures);
    assert(thread_closures == 1);
    assert(process_closures == 0);
    assert(finalizer_completions == 0);
    assert(entry_deletions == 0);

    // Step 27 & 28: Final destroy retry D on main thread succeeds
    remedy_err_t errD = worker_port_destroy(token);
    assert(errD == REMEDY_OK);

    remedy_test_get_final_counts(&finalizer_completions, &entry_deletions, &process_closures, &thread_closures);
    assert(thread_closures == 1);
    assert(process_closures == 1);
    assert(finalizer_completions == 1);
    assert(entry_deletions == 1);

    // Step 29: Assert token snapshot returns false (detached from registry)
    snap_ok = remedy_test_get_entry_snapshot(
        token, &state, &leases, &finalizer_active, &in_registry,
        &is_job_null, &is_thread_null, &is_process_null
    );
    assert(!snap_ok);
    assert(!in_registry);

    // Step 30: Operations on retired token return REMEDY_ERR_INVALID_ARGUMENT and reset out_died
    test_died = true;
    err = worker_port_wait_for_death(token, 100, &test_died);
    assert(err == REMEDY_ERR_INVALID_ARGUMENT);
    assert(!test_died);

    err = worker_port_terminate(token);
    assert(err == REMEDY_ERR_INVALID_ARGUMENT);

    err = worker_port_destroy(token);
    assert(err == REMEDY_ERR_INVALID_ARGUMENT);

#endif // REMEDY_TEST_WORKER_RUNDOWN_SEAM

    // Step 31: Verify baseline handle count restoration (0 net handle leak)
    DWORD current_handles = 0;
    ghc_ok = GetProcessHandleCount(GetCurrentProcess(), &current_handles);
    assert(ghc_ok);
    assert(current_handles == baseline_handles);

    // Step 32: Cleanup temporary directory
    if (fs::exists(test_dir)) {
        fs::remove_all(test_dir);
    }
    assert(!fs::exists(test_dir));

    printf("test_worker_token_rundown passed successfully.\n");
    return 0;
}

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include "remedy/ports/worker_port.h"
#include <stdio.h>
#include <stdlib.h>
#include <assert.h>
#include <chrono>
#include <thread>
#include <string>
#include <filesystem>

#ifdef REMEDY_TEST_STAGED_FAILURE_SEAM
extern "C" {
    void remedy_test_set_fail_stage(int stage);
    HANDLE remedy_test_take_observer_handle(void);
    size_t remedy_test_get_registry_size(void);
}
#endif

namespace fs = std::filesystem;

static void create_dir_clean(const fs::path& p) {
    if (fs::exists(p)) {
        fs::remove_all(p);
    }
    fs::create_directories(p);
}

int main(int argc, char* argv[]) {
    if (argc < 2) {
        fprintf(stderr, "Usage: %s <path_to_remedy_worker_fixture.exe>\n", argv[0]);
        return 1;
    }

    const char* fixture_exe = argv[1];

    // 1. Null output-token rejection
    {
        remedy_worker_config_t config = {0};
        config.executable_path = fixture_exe;
        remedy_err_t err = worker_port_start(&config, nullptr);
        assert(err == REMEDY_ERR_INVALID_ARGUMENT);
    }

    // 2. Invalid config leaves writable token invalid
    {
        remedy_worker_token_t token = 0x123456789ULL;
        remedy_err_t err = worker_port_start(nullptr, &token);
        assert(err == REMEDY_ERR_INVALID_ARGUMENT);
        assert(token == REMEDY_INVALID_WORKER_TOKEN);
    }

    // 3. Unsupported arguments rejected
    {
        remedy_worker_token_t token = 0x123;
        remedy_worker_config_t config = {0};
        config.executable_path = fixture_exe;
        config.arguments = "--invalid-arg";
        remedy_err_t err = worker_port_start(&config, &token);
        assert(err == REMEDY_ERR_NOT_SUPPORTED);
        assert(token == REMEDY_INVALID_WORKER_TOKEN);
    }

    // 4. Unsupported channel nonce rejected
    {
        remedy_worker_token_t token = 0x123;
        remedy_worker_config_t config = {0};
        config.executable_path = fixture_exe;
        config.channel_nonce = "nonce_abc";
        remedy_err_t err = worker_port_start(&config, &token);
        assert(err == REMEDY_ERR_NOT_SUPPORTED);
        assert(token == REMEDY_INVALID_WORKER_TOKEN);
    }

    // 5. Unsupported start timeout rejected
    {
        remedy_worker_token_t token = 0x123;
        remedy_worker_config_t config = {0};
        config.executable_path = fixture_exe;
        config.timeout_ms = 500;
        remedy_err_t err = worker_port_start(&config, &token);
        assert(err == REMEDY_ERR_NOT_SUPPORTED);
        assert(token == REMEDY_INVALID_WORKER_TOKEN);
    }

    // 6. Short malformed paths rejected
    {
        const char* short_paths[] = { "C", "C:", "\\", "relative_path\\fixture.exe" };
        for (const char* sp : short_paths) {
            remedy_worker_token_t token = 0x123;
            remedy_worker_config_t config = {0};
            config.executable_path = sp;
            remedy_err_t err = worker_port_start(&config, &token);
            assert(err == REMEDY_ERR_INVALID_ARGUMENT);
            assert(token == REMEDY_INVALID_WORKER_TOKEN);
        }
    }

    // 7. Non-existent absolute executable path returns failure
    {
        remedy_worker_token_t token = 0x123;
        remedy_worker_config_t config = {0};
        config.executable_path = "C:\\nonexistent_dir_xyz_12345\\fixture.exe";
        remedy_err_t err = worker_port_start(&config, &token);
        assert(err == REMEDY_ERR_IPC_FAILURE);
        assert(token == REMEDY_INVALID_WORKER_TOKEN);
    }

    // 8. Malformed UTF-8 in executable_path
    {
        remedy_worker_token_t token = 0x123;
        remedy_worker_config_t config = {0};
        const char malformed_exec[] = "C:\\test\\\xC0\xAF\xFE\xFF\\fixture.exe";
        config.executable_path = malformed_exec;
        remedy_err_t err = worker_port_start(&config, &token);
        assert(err == REMEDY_ERR_INVALID_ARGUMENT);
        assert(token == REMEDY_INVALID_WORKER_TOKEN);
    }

    // 9. Malformed UTF-8 in working_directory
    {
        remedy_worker_token_t token = 0x123;
        remedy_worker_config_t config = {0};
        config.executable_path = fixture_exe;
        const char malformed_cwd[] = "C:\\test\\\xC0\xAF\xFE\xFF";
        config.working_directory = malformed_cwd;
        remedy_err_t err = worker_port_start(&config, &token);
        assert(err == REMEDY_ERR_INVALID_ARGUMENT);
        assert(token == REMEDY_INVALID_WORKER_TOKEN);
    }

#ifdef REMEDY_TEST_STAGED_FAILURE_SEAM
    // Fully checked test-local raw Win32 process creation warm-up (does not call worker_port_start)
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

    // 10. 7 Post-creation staged failures leave invalid token & dead process, with handle restoration
    for (int stage = 1; stage <= 7; ++stage) {
        assert(remedy_test_take_observer_handle() == NULL);

        fs::path stage_dir = fs::temp_directory_path() / (
            "remedy_test_dir_p" + std::to_string(GetCurrentProcessId()) + "_stage_" + std::to_string(stage)
        );
        create_dir_clean(stage_dir);
        std::string stage_dir_str = stage_dir.string();

        remedy_worker_token_t token = 0x999;
        remedy_worker_config_t config = {0};
        config.executable_path = fixture_exe;
        config.working_directory = stage_dir_str.c_str();

        DWORD baseline_handles = 0;
        BOOL ghc_ok = GetProcessHandleCount(GetCurrentProcess(), &baseline_handles);
        assert(ghc_ok);

        remedy_test_set_fail_stage(stage);
        remedy_err_t err = worker_port_start(&config, &token);
        assert(err != REMEDY_OK);
        assert(token == REMEDY_INVALID_WORKER_TOKEN);

        HANDLE obs = remedy_test_take_observer_handle();
        assert(obs != NULL);

        DWORD wait_obs = WaitForSingleObject(obs, 0);
        assert(wait_obs == WAIT_OBJECT_0);

        BOOL cObs = CloseHandle(obs);
        assert(cObs);
        remedy_test_set_fail_stage(0);

        // Prove suspension through assignment for stages 1 to 4 (primary thread did not execute)
        if (stage <= 4) {
            fs::path stage_marker = stage_dir / "remedy_fixture_marker.tmp";
            assert(!fs::exists(stage_marker));
        }

        DWORD current_handles = 0;
        ghc_ok = GetProcessHandleCount(GetCurrentProcess(), &current_handles);
        assert(ghc_ok);
        assert(current_handles == baseline_handles);

        assert(remedy_test_get_registry_size() == 0);

        if (fs::exists(stage_dir)) {
            fs::remove_all(stage_dir);
        }
        assert(!fs::exists(stage_dir));
    }
#endif

    // 11-18. Normal successful start, marker observation, lifecyle checks, termination & destruction
    {
#ifdef REMEDY_TEST_STAGED_FAILURE_SEAM
        remedy_test_set_fail_stage(0);
        assert(remedy_test_take_observer_handle() == NULL);
#endif

        fs::path test_dir = fs::temp_directory_path() / (
            "remedy_test_dir_p" + std::to_string(GetCurrentProcessId()) + "_success"
        );
        create_dir_clean(test_dir);
        std::string test_dir_str = test_dir.string();
        fs::path marker_path = test_dir / "remedy_fixture_marker.tmp";

        assert(!fs::exists(marker_path));

        remedy_worker_token_t token = REMEDY_INVALID_WORKER_TOKEN;
        remedy_worker_config_t config = {0};
        config.executable_path = fixture_exe;
        config.working_directory = test_dir_str.c_str();

        remedy_err_t err = worker_port_start(&config, &token);
        assert(err == REMEDY_OK);
        assert(token != REMEDY_INVALID_WORKER_TOKEN);

#ifdef REMEDY_TEST_STAGED_FAILURE_SEAM
        assert(remedy_test_take_observer_handle() == NULL);
#endif

        // Fresh marker observation with bounded deadline (steady_clock)
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

        // Worker remains alive before termination
        bool died = true;
        err = worker_port_wait_for_death(token, 50, &died);
        assert(err == REMEDY_ERR_TIMEOUT);
        assert(!died);

        // destroy() rejected before termination (when job handle is open)
        err = worker_port_destroy(token);
        assert(err == REMEDY_ERR_INVALID_ARGUMENT);

        // Contained termination succeeds by closing Job Object
        err = worker_port_terminate(token);
        assert(err == REMEDY_OK);

        // Bounded death observation reports dead
        err = worker_port_wait_for_death(token, 2000, &died);
        assert(err == REMEDY_OK);
        assert(died);

        // destroy() succeeds after confirmed death
        err = worker_port_destroy(token);
        assert(err == REMEDY_OK);

#ifdef REMEDY_TEST_STAGED_FAILURE_SEAM
        assert(remedy_test_get_registry_size() == 0);
#endif

        // Cleanup temporary directory and marker
        if (fs::exists(test_dir)) {
            fs::remove_all(test_dir);
        }
        assert(!fs::exists(test_dir));
    }

    printf("test_worker_start_containment passed successfully.\n");
    return 0;
}

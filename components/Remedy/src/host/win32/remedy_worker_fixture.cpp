#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wchar.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <chrono>
#include <thread>
#include <string>
#include <filesystem>
#include <limits>

namespace fs = std::filesystem;

static bool parse_uint32_exact(const std::string& val_str, uint32_t& out_val) {
    if (val_str.empty()) return false;
    constexpr uint64_t kMax = (std::numeric_limits<uint32_t>::max)();
    uint64_t accum = 0;
    for (char c : val_str) {
        if (c < '0' || c > '9') return false;
        uint64_t digit = static_cast<uint64_t>(c - '0');
        if (accum > (kMax - digit) / 10) {
            return false;
        }
        accum = accum * 10 + digit;
    }
    out_val = static_cast<uint32_t>(accum);
    return true;
}

struct atomic_publish_result {
    bool success{false};
    const char* failed_stage{nullptr};
    DWORD win32_error{0};
    bool temp_removal_verified{false};
    std::string diagnostic;
};

static atomic_publish_result atomic_publish_file(const wchar_t* tmp_path, const wchar_t* final_path, const char* content, size_t length) {
    atomic_publish_result res;
    if (!tmp_path || !final_path || (content == nullptr && length > 0) || length > (std::numeric_limits<DWORD>::max)()) {
        res.failed_stage = "argument_validation";
        res.diagnostic = "Failed stage: argument_validation";
        return res;
    }

    HANDLE hFile = CreateFileW(tmp_path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hFile == INVALID_HANDLE_VALUE) {
        res.failed_stage = "CreateFileW";
        res.win32_error = GetLastError();
        res.diagnostic = "Failed stage: CreateFileW, error: " + std::to_string(res.win32_error);

        std::error_code rm_ec, ex_ec;
        fs::remove(tmp_path, rm_ec);
        if (rm_ec) {
            res.diagnostic += "; Temp remove error on CreateFileW failure: " + rm_ec.message();
        }
        bool exists = fs::exists(tmp_path, ex_ec);
        if (ex_ec) {
            res.diagnostic += "; Temp existence check error on CreateFileW failure: " + ex_ec.message();
            res.temp_removal_verified = false;
        } else {
            res.temp_removal_verified = !exists;
        }
        return res;
    }

    DWORD written = 0;
    BOOL wOk = (length == 0) ? TRUE : WriteFile(hFile, content, static_cast<DWORD>(length), &written, NULL);
    DWORD wErr = wOk ? 0 : GetLastError();

    BOOL fOk = FALSE;
    DWORD fErr = 0;
    if (!wOk) {
        res.failed_stage = "WriteFile";
        res.win32_error = wErr;
        res.diagnostic = "Failed stage: WriteFile, error: " + std::to_string(wErr);
    } else if (length > 0 && written != length) {
        res.failed_stage = "short_write";
        res.win32_error = 0;
        res.diagnostic = "Failed stage: short_write, written: " + std::to_string(written) + ", requested: " + std::to_string(length);
    } else {
        fOk = FlushFileBuffers(hFile);
        fErr = fOk ? 0 : GetLastError();
        if (!fOk) {
            res.failed_stage = "FlushFileBuffers";
            res.win32_error = fErr;
            res.diagnostic = "Failed stage: FlushFileBuffers, error: " + std::to_string(fErr);
        }
    }

    BOOL cOk = CloseHandle(hFile);
    DWORD cErr = cOk ? 0 : GetLastError();
    if (!cOk) {
        if (res.failed_stage == nullptr) {
            res.failed_stage = "CloseHandle";
            res.win32_error = cErr;
            res.diagnostic = "Failed stage: CloseHandle, error: " + std::to_string(cErr);
        } else {
            res.diagnostic += "; CloseHandle secondary failure: " + std::to_string(cErr);
        }
    }

    bool write_flush_close_ok = (wOk && (length == 0 || written == length) && fOk && cOk);

    if (!write_flush_close_ok || !MoveFileExW(tmp_path, final_path, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        if (write_flush_close_ok && res.failed_stage == nullptr) {
            res.failed_stage = "MoveFileExW";
            res.win32_error = GetLastError();
            res.diagnostic = "Failed stage: MoveFileExW, error: " + std::to_string(res.win32_error);
        }

        std::error_code rm_ec, ex_ec;
        fs::remove(tmp_path, rm_ec);
        if (rm_ec) {
            res.diagnostic += "; Temp remove error: " + rm_ec.message();
        }

        bool exists = fs::exists(tmp_path, ex_ec);
        if (ex_ec) {
            res.diagnostic += "; Temp existence check error: " + ex_ec.message();
            res.temp_removal_verified = false;
        } else {
            res.temp_removal_verified = !exists;
        }
        return res;
    }

    std::error_code ex_ec;
    bool exists = fs::exists(tmp_path, ex_ec);
    if (ex_ec) {
        res.failed_stage = "temp_residual_check";
        res.diagnostic = "Failed stage: temp_residual_check, error: " + ex_ec.message();
        res.temp_removal_verified = false;
        return res;
    }

    res.temp_removal_verified = !exists;
    if (!res.temp_removal_verified) {
        res.failed_stage = "temp_residual_check";
        res.diagnostic = "Failed stage: temp_residual_check, temporary file still exists";
        return res;
    }

    res.success = true;
    res.diagnostic = "Success";
    return res;
}

static bool close_handle_checked(HANDLE& h, DWORD& out_err) {
    if (h == NULL) {
        out_err = 0;
        return true;
    }
    BOOL ok = CloseHandle(h);
    out_err = ok ? 0 : GetLastError();
    if (ok) h = NULL;
    return ok;
}

static bool close_handle_checked(HANDLE& h, uint32_t& out_err) {
    DWORD err = 0;
    bool res = close_handle_checked(h, err);
    out_err = static_cast<uint32_t>(err);
    return res;
}

static int run_fixture_hold_wait_existing(HANDLE hEvent, int base_fail_code) {
    if (hEvent == NULL) return base_fail_code;
    DWORD wait_res = WaitForSingleObject(hEvent, INFINITE);
    DWORD cErr = 0;
    bool close_ok = close_handle_checked(hEvent, cErr);

    if (wait_res == WAIT_FAILED) {
        return close_ok ? base_fail_code : (base_fail_code - 1);
    } else {
        return close_ok ? (base_fail_code - 2) : (base_fail_code - 3);
    }
}

static int run_fixture_hold_wait(int base_fail_code) {
    HANDLE hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (hEvent == NULL) return base_fail_code;
    return run_fixture_hold_wait_existing(hEvent, base_fail_code);
}

enum class wait_file_res {
    FOUND,
    TIMEOUT,
    QUERY_ERROR
};

static wait_file_res wait_for_file_steady(const fs::path& file_path, uint32_t timeout_ms, std::string* out_err = nullptr) {
    auto start = std::chrono::steady_clock::now();
    while (std::chrono::steady_clock::now() - start < std::chrono::milliseconds(timeout_ms)) {
        std::error_code ec;
        bool exists = fs::exists(file_path, ec);
        if (ec) {
            if (out_err) *out_err = "Filesystem query error: " + ec.message();
            return wait_file_res::QUERY_ERROR;
        }
        if (exists) {
            return wait_file_res::FOUND;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    return wait_file_res::TIMEOUT;
}

int main(int argc, char* argv[]) {
    if (argc >= 2) {
        std::string flag = argv[1];
        if (flag == "--tree-grandchild" && argc >= 3) {
            std::string cwd_str = argv[2];
            fs::path cwd_path(cwd_str);
            fs::path tmp_path = cwd_path / "remedy_grandchild.tmp";
            fs::path ready_path = cwd_path / "remedy_grandchild.ready";

            HANDLE hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (hEvent == NULL) return 99;

            const char payload[] = "OK\n";
            atomic_publish_result pub = atomic_publish_file(tmp_path.c_str(), ready_path.c_str(), payload, sizeof(payload) - 1);
            if (!pub.success) {
                DWORD cErr = 0;
                bool cOk = close_handle_checked(hEvent, cErr);
                return cOk ? 98 : 198;
            }

            return run_fixture_hold_wait_existing(hEvent, 99);
        }
        else if (flag == "--tree-breakaway-probe") {
            return run_fixture_hold_wait(99);
        }
        else if (flag == "--tree-child" && argc >= 4) {
            std::string cwd_str = argv[2];
            uint32_t root_pid = 0;
            if (!parse_uint32_exact(argv[3], root_pid) || root_pid == 0) {
                return 89;
            }
            DWORD child_pid = GetCurrentProcessId();
            fs::path cwd_path(cwd_str);

            wchar_t self_path[1024] = { 0 };
            DWORD gmf_res = GetModuleFileNameW(NULL, self_path, 1024);
            if (gmf_res == 0 || gmf_res >= 1024) return 90;

            // Spawn grandchild
            STARTUPINFOW si_gc = { sizeof(si_gc) };
            PROCESS_INFORMATION pi_gc = { 0 };
            wchar_t cmd_gc[2048] = { 0 };
            int sw_res = swprintf_s(cmd_gc, 2048, L"\"%s\" --tree-grandchild \"%s\"", self_path, cwd_path.c_str());
            if (sw_res < 0) return 96;

            BOOL pOk = CreateProcessW(self_path, cmd_gc, NULL, NULL, FALSE, 0, NULL, cwd_path.c_str(), &si_gc, &pi_gc);
            if (!pOk) return 91;

            DWORD grandchild_pid = pi_gc.dwProcessId;

            DWORD tc_gc_err = 0, pc_gc_err = 0;
            bool tc_gc_ok = close_handle_checked(pi_gc.hThread, tc_gc_err);
            bool pc_gc_ok = close_handle_checked(pi_gc.hProcess, pc_gc_err);
            if (!tc_gc_ok || !pc_gc_ok) {
                return 92;
            }

            // Attempt breakaway probe
            STARTUPINFOW si_probe = { sizeof(si_probe) };
            PROCESS_INFORMATION pi_probe = { 0 };
            wchar_t cmd_probe[2048] = { 0 };
            sw_res = swprintf_s(cmd_probe, 2048, L"\"%s\" --tree-breakaway-probe \"%s\"", self_path, cwd_path.c_str());
            if (sw_res < 0) return 96;

            BOOL bOk = CreateProcessW(self_path, cmd_probe, NULL, NULL, FALSE, CREATE_BREAKAWAY_FROM_JOB | CREATE_SUSPENDED, NULL, cwd_path.c_str(), &si_probe, &pi_probe);
            DWORD breakaway_last_err = bOk ? 0 : GetLastError();

            uint32_t breakaway_created = 0;
            uint32_t breakaway_pid = 0;
            uint32_t breakaway_cleanup_verified = 0;
            uint32_t breakaway_term_err = 0;
            uint32_t breakaway_wait_res = 0;
            uint32_t breakaway_tc_err = 0;
            uint32_t breakaway_pc_err = 0;

            if (bOk) {
                breakaway_created = 1;
                breakaway_pid = pi_probe.dwProcessId;

                BOOL term_ok = TerminateProcess(pi_probe.hProcess, 1);
                breakaway_term_err = term_ok ? 0 : GetLastError();

                DWORD wRes = WaitForSingleObject(pi_probe.hProcess, 2000);
                breakaway_wait_res = wRes;

                bool tc_ok = close_handle_checked(pi_probe.hThread, breakaway_tc_err);
                bool pc_ok = close_handle_checked(pi_probe.hProcess, breakaway_pc_err);

                bool death_verified = (wRes == WAIT_OBJECT_0);
                if (death_verified && tc_ok && pc_ok) {
                    breakaway_cleanup_verified = 1;
                } else {
                    breakaway_cleanup_verified = 0;
                }
            }

            // Create child hold event BEFORE publication / waiting
            HANDLE hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (hEvent == NULL) return 99;

            // Wait for grandchild readiness
            fs::path gc_ready_path = cwd_path / "remedy_grandchild.ready";
            std::string wf_err;
            if (wait_for_file_steady(gc_ready_path, 5000, &wf_err) != wait_file_res::FOUND) {
                DWORD cErr = 0;
                bool cOk = close_handle_checked(hEvent, cErr);
                return cOk ? 93 : 193;
            }

            if (breakaway_created == 0) {
                fs::path manifest_tmp = cwd_path / "remedy_tree_manifest.tmp";
                fs::path manifest_ready = cwd_path / "remedy_tree_manifest.ready";

                char payload[1024] = { 0 };
                int len = snprintf(payload, sizeof(payload),
                    "ROOT_PID=%lu\n"
                    "CHILD_PID=%lu\n"
                    "GRANDCHILD_PID=%lu\n"
                    "BREAKAWAY_CREATED=0\n"
                    "BREAKAWAY_PID=0\n"
                    "BREAKAWAY_CLEANUP_VERIFIED=0\n"
                    "BREAKAWAY_LAST_ERROR=%lu\n",
                    static_cast<unsigned long>(root_pid),
                    static_cast<unsigned long>(child_pid),
                    static_cast<unsigned long>(grandchild_pid),
                    static_cast<unsigned long>(breakaway_last_err));

                if (len < 0 || len >= static_cast<int>(sizeof(payload))) {
                    DWORD cErr = 0;
                    bool cOk = close_handle_checked(hEvent, cErr);
                    return cOk ? 97 : 197;
                }

                atomic_publish_result pub = atomic_publish_file(manifest_tmp.c_str(), manifest_ready.c_str(), payload, static_cast<size_t>(len));
                if (!pub.success) {
                    DWORD cErr = 0;
                    bool cOk = close_handle_checked(hEvent, cErr);
                    return cOk ? 94 : 194;
                }
            } else {
                fs::path emer_tmp = cwd_path / "remedy_breakaway_emergency.tmp";
                fs::path emer_ready = cwd_path / "remedy_breakaway_emergency.ready";

                char payload[1024] = { 0 };
                int len = snprintf(payload, sizeof(payload),
                    "ROOT_PID=%lu\n"
                    "CHILD_PID=%lu\n"
                    "GRANDCHILD_PID=%lu\n"
                    "BREAKAWAY_CREATED=1\n"
                    "BREAKAWAY_PID=%lu\n"
                    "BREAKAWAY_CLEANUP_VERIFIED=%lu\n"
                    "BREAKAWAY_LAST_ERROR=0\n"
                    "BREAKAWAY_TERMINATE_ERROR=%lu\n"
                    "BREAKAWAY_WAIT_RESULT=%lu\n"
                    "BREAKAWAY_THREAD_CLOSE_ERROR=%lu\n"
                    "BREAKAWAY_PROCESS_CLOSE_ERROR=%lu\n",
                    static_cast<unsigned long>(root_pid),
                    static_cast<unsigned long>(child_pid),
                    static_cast<unsigned long>(grandchild_pid),
                    static_cast<unsigned long>(breakaway_pid),
                    static_cast<unsigned long>(breakaway_cleanup_verified),
                    static_cast<unsigned long>(breakaway_term_err),
                    static_cast<unsigned long>(breakaway_wait_res),
                    static_cast<unsigned long>(breakaway_tc_err),
                    static_cast<unsigned long>(breakaway_pc_err));

                if (len < 0 || len >= static_cast<int>(sizeof(payload))) {
                    DWORD cErr = 0;
                    bool cOk = close_handle_checked(hEvent, cErr);
                    return cOk ? 97 : 197;
                }

                atomic_publish_result pub = atomic_publish_file(emer_tmp.c_str(), emer_ready.c_str(), payload, static_cast<size_t>(len));
                if (!pub.success) {
                    DWORD cErr = 0;
                    bool cOk = close_handle_checked(hEvent, cErr);
                    return cOk ? 95 : 195;
                }
            }

            return run_fixture_hold_wait_existing(hEvent, 99);
        }
    }

    // Default mode & Tree Root mode (argc == 1)
    wchar_t cwd[1024] = { 0 };
    DWORD len = GetCurrentDirectoryW(1024, cwd);
    if (len == 0 || len >= 1024) {
        return 1;
    }

    fs::path cwd_path(cwd);
    fs::path mode_path = cwd_path / "remedy_fixture_tree_mode.tmp";

    std::error_code mode_ec;
    bool mode_exists = fs::exists(mode_path, mode_ec);
    if (mode_ec) {
        return 79;
    }

    if (!mode_exists) {
        // Preserved exact default fixture behavior byte-for-byte
        wchar_t marker_path[1024] = { 0 };
        if (_snwprintf(marker_path, 1024, L"%s\\remedy_fixture_marker.tmp", cwd) < 0) {
            return 2;
        }

        HANDLE hFile = CreateFileW(
            marker_path,
            GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            NULL,
            CREATE_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            NULL
        );

        if (hFile == INVALID_HANDLE_VALUE) {
            return 3;
        }

        const char marker_contents[] = "OK";
        DWORD bytes_written = 0;
        BOOL write_ok = WriteFile(hFile, marker_contents, (DWORD)sizeof(marker_contents) - 1, &bytes_written, NULL);
        if (!write_ok || bytes_written != sizeof(marker_contents) - 1) {
            BOOL close_ok = CloseHandle(hFile);
            if (!close_ok) {
                return 8;
            }
            return 4;
        }

        BOOL flush_ok = FlushFileBuffers(hFile);
        if (!flush_ok) {
            BOOL close_ok = CloseHandle(hFile);
            if (!close_ok) {
                return 9;
            }
            return 5;
        }

        BOOL close_ok = CloseHandle(hFile);
        if (!close_ok) {
            return 6;
        }

        // Genuinely non-settling Win32 wait until externally terminated
        DWORD wait_res = WaitForSingleObject(GetCurrentProcess(), INFINITE);
        if (wait_res != WAIT_OBJECT_0) {
            return 7;
        }

        return 0;
    }

    // Tree Root Mode
    std::string mode_str;
    FILE* fMode = nullptr;
    if (_wfopen_s(&fMode, mode_path.c_str(), L"r") == 0 && fMode) {
        char buf[256] = { 0 };
        if (fgets(buf, sizeof(buf), fMode)) {
            mode_str = buf;
            while (!mode_str.empty() && (mode_str.back() == '\r' || mode_str.back() == '\n' || mode_str.back() == ' ')) {
                mode_str.pop_back();
            }
        }
        bool err = (ferror(fMode) != 0);
        int fc_res = fclose(fMode);
        if (err || fc_res != 0) return 78;
    } else {
        return 78;
    }

    if (mode_str != "HOLD_ROOT" && mode_str != "ROOT_EXIT_EARLY") {
        return 78;
    }

    DWORD root_pid = GetCurrentProcessId();
    wchar_t self_path[1024] = { 0 };
    DWORD gmf_res = GetModuleFileNameW(NULL, self_path, 1024);
    if (gmf_res == 0 || gmf_res >= 1024) return 80;

    // Spawn child
    STARTUPINFOW si_ch = { sizeof(si_ch) };
    PROCESS_INFORMATION pi_ch = { 0 };
    wchar_t cmd_ch[2048] = { 0 };
    int sw_res = swprintf_s(cmd_ch, 2048, L"\"%s\" --tree-child \"%s\" %lu", self_path, cwd_path.c_str(), static_cast<unsigned long>(root_pid));
    if (sw_res < 0) return 88;

    BOOL pOk = CreateProcessW(self_path, cmd_ch, NULL, NULL, FALSE, 0, NULL, cwd_path.c_str(), &si_ch, &pi_ch);
    if (!pOk) return 81;

    DWORD tc_ch_err = 0, pc_ch_err = 0;
    bool tc_ch_ok = close_handle_checked(pi_ch.hThread, tc_ch_err);
    bool pc_ch_ok = close_handle_checked(pi_ch.hProcess, pc_ch_err);
    if (!tc_ch_ok || !pc_ch_ok) {
        return 82;
    }

    // Wait for child manifest or emergency manifest
    fs::path normal_manifest = cwd_path / "remedy_tree_manifest.ready";
    fs::path emergency_manifest = cwd_path / "remedy_breakaway_emergency.ready";

    auto start = std::chrono::steady_clock::now();
    while (std::chrono::steady_clock::now() - start < std::chrono::milliseconds(5000)) {
        std::error_code ec1, ec2;
        bool norm_e = fs::exists(normal_manifest, ec1);
        bool emer_e = fs::exists(emergency_manifest, ec2);
        if (ec1 || ec2) return 83;
        if (norm_e || emer_e) break;
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }

    std::error_code ec_norm, ec_emer;
    bool norm_exists = fs::exists(normal_manifest, ec_norm);
    bool emer_exists = fs::exists(emergency_manifest, ec_emer);
    if (ec_norm || ec_emer) return 83;

    if (norm_exists && emer_exists) return 84;
    if (!norm_exists && !emer_exists) return 84;

    if (emer_exists) {
        return run_fixture_hold_wait(99);
    }

    if (mode_str == "HOLD_ROOT") {
        return run_fixture_hold_wait(99);
    }
    else if (mode_str == "ROOT_EXIT_EARLY") {
        fs::path release_marker = cwd_path / "remedy_root_exit.release";
        std::string wf_err;
        if (wait_for_file_steady(release_marker, 5000, &wf_err) != wait_file_res::FOUND) {
            return run_fixture_hold_wait(99);
        }
        return 0;
    }

    return 85;
}

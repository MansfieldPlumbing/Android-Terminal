#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include "remedy/ports/worker_port.h"
#include <stdio.h>
#include <stdlib.h>
#include <chrono>
#include <thread>
#include <string>
#include <filesystem>
#include <unordered_map>
#include <unordered_set>
#include <limits>

namespace fs = std::filesystem;

#ifdef REMEDY_TEST_WORKER_TREE_SEAM
extern "C" {
    bool remedy_test_worker_job_get_policy(
        remedy_worker_token_t token,
        uint32_t* out_limit_flags,
        uint32_t* out_active_processes
    );
    bool remedy_test_worker_job_contains_pid(
        remedy_worker_token_t token,
        uint32_t pid,
        bool* out_contains
    );
}
#endif

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

static bool create_dir_clean_checked(const fs::path& p, std::string& out_diag) {
    std::error_code ec_ex, ec_rm, ec_post, ec_cr, ec_final;
    bool exists_init = fs::exists(p, ec_ex);
    if (ec_ex) {
        out_diag = "Initial directory existence check failed: " + ec_ex.message();
        return false;
    }
    if (exists_init) {
        fs::remove_all(p, ec_rm);
        if (ec_rm) {
            out_diag = "Directory remove_all failed: " + ec_rm.message();
            return false;
        }
        bool exists_post_rm = fs::exists(p, ec_post);
        if (ec_post) {
            out_diag = "Directory post-remove existence check failed: " + ec_post.message();
            return false;
        }
        if (exists_post_rm) {
            out_diag = "Directory still exists after remove_all";
            return false;
        }
    }
    fs::create_directories(p, ec_cr);
    if (ec_cr) {
        out_diag = "Directory create_directories failed: " + ec_cr.message();
        return false;
    }
    bool exists_final = fs::exists(p, ec_final);
    if (ec_final) {
        out_diag = "Directory final existence check failed: " + ec_final.message();
        return false;
    }
    if (!exists_final) {
        out_diag = "Directory does not exist after create_directories";
        return false;
    }
    return true;
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

static std::string classify_wait_result(DWORD wait_res, DWORD last_err) {
    if (wait_res == WAIT_OBJECT_0) return "WAIT_OBJECT_0";
    if (wait_res == WAIT_TIMEOUT) return "WAIT_TIMEOUT";
    if (wait_res == WAIT_FAILED) return "WAIT_FAILED (error: " + std::to_string(last_err) + ")";
    return "WAIT_UNEXPECTED (" + std::to_string(wait_res) + ")";
}

struct manifest_data {
    bool is_emergency{false};
    uint32_t root_pid{0};
    uint32_t child_pid{0};
    uint32_t grandchild_pid{0};
    uint32_t breakaway_created{0};
    uint32_t breakaway_pid{0};
    uint32_t breakaway_cleanup_verified{0};
    uint32_t breakaway_last_error{0};
    uint32_t breakaway_terminate_error{0};
    uint32_t breakaway_wait_result{0};
    uint32_t breakaway_thread_close_error{0};
    uint32_t breakaway_process_close_error{0};
};

static bool parse_manifest_file(const fs::path& path, bool is_emergency, manifest_data& out_data) {
    FILE* f = nullptr;
    if (_wfopen_s(&f, path.c_str(), L"r") != 0 || !f) {
        return false;
    }

    static const std::unordered_set<std::string> normal_keys = {
        "ROOT_PID", "CHILD_PID", "GRANDCHILD_PID",
        "BREAKAWAY_CREATED", "BREAKAWAY_PID", "BREAKAWAY_CLEANUP_VERIFIED",
        "BREAKAWAY_LAST_ERROR"
    };

    static const std::unordered_set<std::string> emergency_keys = {
        "ROOT_PID", "CHILD_PID", "GRANDCHILD_PID",
        "BREAKAWAY_CREATED", "BREAKAWAY_PID", "BREAKAWAY_CLEANUP_VERIFIED",
        "BREAKAWAY_LAST_ERROR", "BREAKAWAY_TERMINATE_ERROR", "BREAKAWAY_WAIT_RESULT",
        "BREAKAWAY_THREAD_CLOSE_ERROR", "BREAKAWAY_PROCESS_CLOSE_ERROR"
    };

    const auto& allowed_keys = is_emergency ? emergency_keys : normal_keys;

    bool parse_ok = true;
    std::unordered_map<std::string, uint32_t> kv;
    char line[256] = { 0 };
    while (parse_ok && fgets(line, sizeof(line), f)) {
        std::string l = line;
        while (!l.empty() && (l.back() == '\r' || l.back() == '\n')) l.pop_back();
        if (l.empty()) continue;

        size_t eq = l.find('=');
        if (eq == std::string::npos || eq == 0 || eq == l.length() - 1) {
            parse_ok = false;
            break;
        }

        std::string key = l.substr(0, eq);
        std::string val = l.substr(eq + 1);

        if (allowed_keys.find(key) == allowed_keys.end() || kv.find(key) != kv.end()) {
            parse_ok = false;
            break;
        }

        uint32_t uval = 0;
        if (!parse_uint32_exact(val, uval)) {
            parse_ok = false;
            break;
        }

        kv[key] = uval;
    }

    if (parse_ok && ferror(f) != 0) {
        parse_ok = false;
    }

    int fc_res = fclose(f);
    if (fc_res != 0) {
        parse_ok = false;
    }

    if (!parse_ok) return false;
    if (kv.size() != allowed_keys.size()) return false;

    out_data.is_emergency = is_emergency;

    auto get_field = [&](const std::string& k, uint32_t& out_v) -> bool {
        auto it = kv.find(k);
        if (it == kv.end()) return false;
        out_v = it->second;
        return true;
    };

    if (!get_field("ROOT_PID", out_data.root_pid) ||
        !get_field("CHILD_PID", out_data.child_pid) ||
        !get_field("GRANDCHILD_PID", out_data.grandchild_pid) ||
        !get_field("BREAKAWAY_CREATED", out_data.breakaway_created) ||
        !get_field("BREAKAWAY_PID", out_data.breakaway_pid) ||
        !get_field("BREAKAWAY_CLEANUP_VERIFIED", out_data.breakaway_cleanup_verified)) {
        return false;
    }

    if (out_data.breakaway_created > 1 || out_data.breakaway_cleanup_verified > 1) {
        return false;
    }

    if (!is_emergency) {
        if (!get_field("BREAKAWAY_LAST_ERROR", out_data.breakaway_last_error)) return false;
        if (out_data.breakaway_created != 0 || out_data.breakaway_pid != 0 || out_data.breakaway_cleanup_verified != 0) {
            return false;
        }
    } else {
        if (!get_field("BREAKAWAY_LAST_ERROR", out_data.breakaway_last_error) ||
            !get_field("BREAKAWAY_TERMINATE_ERROR", out_data.breakaway_terminate_error) ||
            !get_field("BREAKAWAY_WAIT_RESULT", out_data.breakaway_wait_result) ||
            !get_field("BREAKAWAY_THREAD_CLOSE_ERROR", out_data.breakaway_thread_close_error) ||
            !get_field("BREAKAWAY_PROCESS_CLOSE_ERROR", out_data.breakaway_process_close_error)) {
            return false;
        }
        if (out_data.breakaway_created != 1 || out_data.breakaway_pid == 0 || out_data.breakaway_last_error != 0) {
            return false;
        }
        bool breakaway_death_verified = (out_data.breakaway_wait_result == WAIT_OBJECT_0);
        uint32_t expected_cleanup_verified = (breakaway_death_verified &&
            out_data.breakaway_thread_close_error == 0 &&
            out_data.breakaway_process_close_error == 0) ? 1 : 0;

        if (out_data.breakaway_cleanup_verified != expected_cleanup_verified) {
            return false;
        }
    }

    if (out_data.root_pid == 0 || out_data.child_pid == 0 || out_data.grandchild_pid == 0) {
        return false;
    }
    if (out_data.root_pid == out_data.child_pid ||
        out_data.root_pid == out_data.grandchild_pid ||
        out_data.child_pid == out_data.grandchild_pid) {
        return false;
    }

    return true;
}

struct test_scenario_context {
    remedy_worker_token_t token{REMEDY_INVALID_WORKER_TOKEN};
    DWORD root_pid{0};
    DWORD child_pid{0};
    DWORD grandchild_pid{0};
    DWORD breakaway_pid{0};
    HANDLE hRoot{NULL};        // SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION
    HANDLE hChild{NULL};       // SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION
    HANDLE hGrandchild{NULL};  // SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION
    HANDLE hBreakaway{NULL};   // Pinned with PROCESS_TERMINATE | SYNCHRONIZE when !breakaway_death_verified
    bool job_termination_succeeded{false};
    bool root_death_verified{false};
    std::string first_error;
    std::string cleanup_errors;

    void record_error(const std::string& msg) {
        if (first_error.empty()) {
            first_error = msg;
        } else {
            if (!cleanup_errors.empty()) cleanup_errors += "; ";
            cleanup_errors += msg;
        }
    }

    void record_cleanup_error(const std::string& msg) {
        if (!cleanup_errors.empty()) cleanup_errors += "; ";
        cleanup_errors += msg;
    }

    bool execute_cleanup(const fs::path& test_dir) {
        // 1. Unverified breakaway process cleanup (ANY initial result other than WAIT_OBJECT_0 enters bounded recovery)
        if (hBreakaway != NULL) {
            DWORD wRes1 = WaitForSingleObject(hBreakaway, 0);
            DWORD wErr1 = (wRes1 == WAIT_FAILED) ? GetLastError() : 0;

            bool death_verified = (wRes1 == WAIT_OBJECT_0);
            BOOL t1Ok = TRUE; DWORD t1Err = 0;
            DWORD wRes2 = wRes1; DWORD wErr2 = wErr1;
            BOOL t2Ok = TRUE; DWORD t2Err = 0;
            DWORD wRes3 = wRes1; DWORD wErr3 = wErr1;

            if (!death_verified) {
                t1Ok = TerminateProcess(hBreakaway, 1);
                t1Err = t1Ok ? 0 : GetLastError();
                wRes2 = WaitForSingleObject(hBreakaway, 1000);
                wErr2 = (wRes2 == WAIT_FAILED) ? GetLastError() : 0;

                if (wRes2 == WAIT_OBJECT_0) {
                    death_verified = true;
                } else {
                    t2Ok = TerminateProcess(hBreakaway, 1);
                    t2Err = t2Ok ? 0 : GetLastError();
                    wRes3 = WaitForSingleObject(hBreakaway, 1000);
                    wErr3 = (wRes3 == WAIT_FAILED) ? GetLastError() : 0;
                    if (wRes3 == WAIT_OBJECT_0) {
                        death_verified = true;
                    }
                }
            }

            if (!death_verified) {
                record_cleanup_error("Catastrophic breakaway cleanup failure: initial_wait=" + classify_wait_result(wRes1, wErr1) +
                    ", t1_ok=" + std::to_string(t1Ok) + " (err=" + std::to_string(t1Err) +
                    "), post_t1_wait=" + classify_wait_result(wRes2, wErr2) +
                    ", t2_ok=" + std::to_string(t2Ok) + " (err=" + std::to_string(t2Err) +
                    "), final_wait=" + classify_wait_result(wRes3, wErr3));
            }

            DWORD cErr = 0;
            if (!close_handle_checked(hBreakaway, cErr)) {
                record_cleanup_error("CloseHandle hBreakaway failed: " + std::to_string(cErr));
            }
        }

        // 2. Initial Worker Job Termination Attempt (ONLY if token valid and job termination not yet succeeded)
        if (token != REMEDY_INVALID_WORKER_TOKEN && !job_termination_succeeded) {
            remedy_err_t term_err = worker_port_terminate(token);
            if (term_err == REMEDY_OK) {
                job_termination_succeeded = true;
                bool died = false;
                remedy_err_t werr = worker_port_wait_for_death(token, 2000, &died);
                if (werr == REMEDY_OK && died) {
                    root_death_verified = true;
                } else {
                    record_cleanup_error("Initial worker_port_wait_for_death failed in cleanup");
                }
            } else {
                record_cleanup_error("Initial worker_port_terminate returned " + std::to_string(term_err));
            }
        }

        // 3. Checked Process Handle Cleanup (NEVER pass limited-rights observation handles to TerminateProcess)
        HANDLE handles[3] = { hRoot, hChild, hGrandchild };
        DWORD pids[3] = { root_pid, child_pid, grandchild_pid };
        for (int i = 0; i < 3; ++i) {
            HANDLE hObs = handles[i];
            DWORD pid = pids[i];

            if (hObs != NULL) {
                DWORD wRes = WaitForSingleObject(hObs, 1000);
                DWORD wErr = (wRes == WAIT_FAILED) ? GetLastError() : 0;
                if (wRes == WAIT_TIMEOUT && pid != 0) {
                    HANDLE hTerm = OpenProcess(PROCESS_TERMINATE | SYNCHRONIZE, FALSE, pid);
                    if (hTerm != NULL) {
                        DWORD zWait = WaitForSingleObject(hTerm, 0);
                        DWORD zErr = (zWait == WAIT_FAILED) ? GetLastError() : 0;
                        if (zWait == WAIT_TIMEOUT) {
                            if (TerminateProcess(hTerm, 1)) {
                                DWORD termWait = WaitForSingleObject(hTerm, 1000);
                                DWORD tErr = (termWait == WAIT_FAILED) ? GetLastError() : 0;
                                if (termWait != WAIT_OBJECT_0) {
                                    record_cleanup_error("Emergency handle exit wait classified for PID " + std::to_string(pid) + ": " + classify_wait_result(termWait, tErr));
                                }
                            } else {
                                record_cleanup_error("Emergency TerminateProcess failed for PID " + std::to_string(pid) + ": " + std::to_string(GetLastError()));
                            }
                        } else if (zWait != WAIT_OBJECT_0) {
                            record_cleanup_error("Emergency zero-time wait classified for PID " + std::to_string(pid) + ": " + classify_wait_result(zWait, zErr));
                        }
                        DWORD cErr = 0;
                        if (!close_handle_checked(hTerm, cErr)) record_cleanup_error("CloseHandle emergency handle failed for PID " + std::to_string(pid) + ": " + std::to_string(cErr));
                    } else {
                        record_cleanup_error("Emergency OpenProcess failed for PID " + std::to_string(pid) + ": " + std::to_string(GetLastError()));
                    }
                    wRes = WaitForSingleObject(hObs, 1000);
                    wErr = (wRes == WAIT_FAILED) ? GetLastError() : 0;
                }
                if (wRes != WAIT_OBJECT_0) {
                    record_cleanup_error("Observation process handle wait classified for PID " + std::to_string(pid) + ": " + classify_wait_result(wRes, wErr));
                }
            } else if (pid != 0 && !job_termination_succeeded) {
                // Null original handle with nonzero PID: checked termination ONLY if Job termination has not succeeded
                HANDLE hTerm = OpenProcess(PROCESS_TERMINATE | SYNCHRONIZE, FALSE, pid);
                if (hTerm != NULL) {
                    DWORD zWait = WaitForSingleObject(hTerm, 0);
                    DWORD zErr = (zWait == WAIT_FAILED) ? GetLastError() : 0;
                    if (zWait == WAIT_TIMEOUT) {
                        if (TerminateProcess(hTerm, 1)) {
                            DWORD termWait = WaitForSingleObject(hTerm, 1000);
                            DWORD tErr = (termWait == WAIT_FAILED) ? GetLastError() : 0;
                            if (termWait != WAIT_OBJECT_0) {
                                record_cleanup_error("Null-handle path exit wait classified for PID " + std::to_string(pid) + ": " + classify_wait_result(termWait, tErr));
                            }
                        } else {
                            record_cleanup_error("Null-handle path TerminateProcess failed for PID " + std::to_string(pid) + ": " + std::to_string(GetLastError()));
                        }
                    } else if (zWait != WAIT_OBJECT_0) {
                        record_cleanup_error("Null-handle path zero-time wait classified for PID " + std::to_string(pid) + ": " + classify_wait_result(zWait, zErr));
                    }
                    DWORD cErr = 0;
                    if (!close_handle_checked(hTerm, cErr)) record_cleanup_error("CloseHandle null-handle path handle failed for PID " + std::to_string(pid) + ": " + std::to_string(cErr));
                } else {
                    record_cleanup_error("Null-handle path OpenProcess failed for PID " + std::to_string(pid) + ": " + std::to_string(GetLastError()));
                }
            }
        }

        // 4. One Bounded Retry for Worker Termination if initial attempt failed and token valid
        if (token != REMEDY_INVALID_WORKER_TOKEN) {
            if (!job_termination_succeeded) {
                remedy_err_t retry_err = worker_port_terminate(token);
                if (retry_err == REMEDY_OK) {
                    job_termination_succeeded = true;
                } else {
                    record_cleanup_error("Retry worker_port_terminate returned " + std::to_string(retry_err));
                }
            }
            if (!root_death_verified) {
                bool died = false;
                remedy_err_t werr = worker_port_wait_for_death(token, 2000, &died);
                if (werr == REMEDY_OK && died) {
                    root_death_verified = true;
                } else {
                    record_cleanup_error("Post-emergency worker_port_wait_for_death failed");
                }
            }
        }

        // 5. Destroy worker token ONLY when job termination succeeded and root death verified
        if (token != REMEDY_INVALID_WORKER_TOKEN) {
            if (job_termination_succeeded && root_death_verified) {
                remedy_err_t des_err = worker_port_destroy(token);
                if (des_err == REMEDY_OK) {
                    token = REMEDY_INVALID_WORKER_TOKEN;
                } else {
                    record_cleanup_error("worker_port_destroy returned " + std::to_string(des_err));
                }
            } else {
                record_cleanup_error("Skipping worker_port_destroy because job_termination_succeeded or root_death_verified is false");
            }
        }

        // 6. Close original observation process handles exactly once with checked CloseHandle
        DWORD cErr = 0;
        if (!close_handle_checked(hRoot, cErr)) record_cleanup_error("CloseHandle hRoot failed: " + std::to_string(cErr));
        if (!close_handle_checked(hChild, cErr)) record_cleanup_error("CloseHandle hChild failed: " + std::to_string(cErr));
        if (!close_handle_checked(hGrandchild, cErr)) record_cleanup_error("CloseHandle hGrandchild failed: " + std::to_string(cErr));

        // 7. Nonthrowing filesystem cleanup using separate error codes and verification
        std::error_code ec_ex, ec_rm, ec_post;
        bool exists_before = fs::exists(test_dir, ec_ex);
        if (ec_ex) {
            record_cleanup_error("Cleanup directory initial existence check failed: " + ec_ex.message());
        } else if (exists_before) {
            fs::remove_all(test_dir, ec_rm);
            if (ec_rm) {
                record_cleanup_error("Cleanup directory remove_all failed: " + ec_rm.message());
            }
            bool exists_after = fs::exists(test_dir, ec_post);
            if (ec_post) {
                record_cleanup_error("Cleanup directory post-removal existence check failed: " + ec_post.message());
            } else if (exists_after) {
                record_cleanup_error("Cleanup directory still exists after removal");
            }
        }

        return first_error.empty() && cleanup_errors.empty();
    }
};

static bool run_scenario_a(const char* fixture_exe, std::string& out_primary_err, std::string& out_cleanup_err) {
    test_scenario_context ctx;

    fs::path test_dir = fs::temp_directory_path() / (
        "remedy_test_tree_scen_a_p" + std::to_string(GetCurrentProcessId())
    );
    std::string cd_diag;
    if (!create_dir_clean_checked(test_dir, cd_diag)) {
        out_primary_err = "Directory preparation failed: " + cd_diag;
        ctx.execute_cleanup(test_dir);
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
    std::string test_dir_str = test_dir.string();

    fs::path mode_path = test_dir / "remedy_fixture_tree_mode.tmp";
    const char mode_content[] = "HOLD_ROOT";
    atomic_publish_result pub = atomic_publish_file(
        (test_dir / "mode.tmp").c_str(),
        mode_path.c_str(),
        mode_content,
        sizeof(mode_content) - 1
    );
    if (!pub.success) {
        ctx.record_error("Failed to publish mode file: " + pub.diagnostic);
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    remedy_worker_config_t config = { 0 };
    config.executable_path = fixture_exe;
    config.working_directory = test_dir_str.c_str();

    remedy_err_t start_err = worker_port_start(&config, &ctx.token);
    if (start_err != REMEDY_OK || ctx.token == REMEDY_INVALID_WORKER_TOKEN) {
        ctx.record_error("worker_port_start failed: " + std::to_string(start_err));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    fs::path normal_manifest = test_dir / "remedy_tree_manifest.ready";
    fs::path emergency_manifest = test_dir / "remedy_breakaway_emergency.ready";

    auto start_time = std::chrono::steady_clock::now();
    while (std::chrono::steady_clock::now() - start_time < std::chrono::milliseconds(5000)) {
        std::error_code ec1, ec2;
        bool norm_e = fs::exists(normal_manifest, ec1);
        bool emer_e = fs::exists(emergency_manifest, ec2);
        if (ec1 || ec2) {
            ctx.record_error("Polling filesystem query error: " + (ec1 ? ec1.message() : ec2.message()));
            ctx.execute_cleanup(test_dir);
            out_primary_err = ctx.first_error;
            out_cleanup_err = ctx.cleanup_errors;
            return false;
        }
        if (norm_e || emer_e) break;
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }

    std::error_code ec_norm, ec_emer;
    bool normal_found = fs::exists(normal_manifest, ec_norm);
    bool emergency_found = fs::exists(emergency_manifest, ec_emer);
    if (ec_norm || ec_emer) {
        ctx.record_error("Post-polling filesystem query error: " + (ec_norm ? ec_norm.message() : ec_emer.message()));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    if (normal_found && emergency_found) {
        ctx.record_error("Both normal and emergency manifests exist simultaneously");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    if (!normal_found && !emergency_found) {
        ctx.record_error("Neither normal nor emergency manifest appeared within timeout");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    manifest_data mdata;
    if (emergency_found) {
        if (!parse_manifest_file(emergency_manifest, true, mdata)) {
            ctx.record_error("Failed to parse emergency manifest file");
            ctx.execute_cleanup(test_dir);
            out_primary_err = ctx.first_error;
            out_cleanup_err = ctx.cleanup_errors;
            return false;
        }

        ctx.root_pid = mdata.root_pid;
        ctx.child_pid = mdata.child_pid;
        ctx.grandchild_pid = mdata.grandchild_pid;
        ctx.breakaway_pid = mdata.breakaway_pid;

        ctx.hRoot = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, ctx.root_pid);
        if (ctx.hRoot == NULL) {
            ctx.record_cleanup_error("Emergency OpenProcess failed for root PID " + std::to_string(ctx.root_pid) + ", error: " + std::to_string(GetLastError()));
        }

        ctx.hChild = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, ctx.child_pid);
        if (ctx.hChild == NULL) {
            ctx.record_cleanup_error("Emergency OpenProcess failed for child PID " + std::to_string(ctx.child_pid) + ", error: " + std::to_string(GetLastError()));
        }

        ctx.hGrandchild = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, ctx.grandchild_pid);
        if (ctx.hGrandchild == NULL) {
            ctx.record_cleanup_error("Emergency OpenProcess failed for grandchild PID " + std::to_string(ctx.grandchild_pid) + ", error: " + std::to_string(GetLastError()));
        }

        ctx.record_error("Emergency breakaway creation detected (BREAKAWAY_CREATED=1)");

        bool death_verified = (mdata.breakaway_wait_result == WAIT_OBJECT_0);
        if (!death_verified) {
            ctx.hBreakaway = OpenProcess(PROCESS_TERMINATE | SYNCHRONIZE, FALSE, ctx.breakaway_pid);
            if (ctx.hBreakaway == NULL) {
                ctx.record_error("OpenProcess failed for unverified breakaway PID " + std::to_string(ctx.breakaway_pid) + ": " + std::to_string(GetLastError()));
            }
        }

        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    // Normal manifest path
    if (!parse_manifest_file(normal_manifest, false, mdata)) {
        ctx.record_error("Failed to parse normal manifest file");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    ctx.root_pid = mdata.root_pid;
    ctx.child_pid = mdata.child_pid;
    ctx.grandchild_pid = mdata.grandchild_pid;
    ctx.breakaway_pid = mdata.breakaway_pid;

    ctx.hRoot = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, ctx.root_pid);
    if (!ctx.hRoot) {
        ctx.record_error("Failed to open hRoot (PID " + std::to_string(ctx.root_pid) + "): " + std::to_string(GetLastError()));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
    ctx.hChild = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, ctx.child_pid);
    if (!ctx.hChild) {
        ctx.record_error("Failed to open hChild (PID " + std::to_string(ctx.child_pid) + "): " + std::to_string(GetLastError()));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
    ctx.hGrandchild = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, ctx.grandchild_pid);
    if (!ctx.hGrandchild) {
        ctx.record_error("Failed to open hGrandchild (PID " + std::to_string(ctx.grandchild_pid) + "): " + std::to_string(GetLastError()));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    DWORD wRoot = WaitForSingleObject(ctx.hRoot, 0);
    DWORD wRootErr = (wRoot == WAIT_FAILED) ? GetLastError() : 0;
    if (wRoot != WAIT_TIMEOUT) {
        ctx.record_error("Root process liveness wait classified: " + classify_wait_result(wRoot, wRootErr));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    DWORD wChild = WaitForSingleObject(ctx.hChild, 0);
    DWORD wChildErr = (wChild == WAIT_FAILED) ? GetLastError() : 0;
    if (wChild != WAIT_TIMEOUT) {
        ctx.record_error("Child process liveness wait classified: " + classify_wait_result(wChild, wChildErr));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    DWORD wGc = WaitForSingleObject(ctx.hGrandchild, 0);
    DWORD wGcErr = (wGc == WAIT_FAILED) ? GetLastError() : 0;
    if (wGc != WAIT_TIMEOUT) {
        ctx.record_error("Grandchild process liveness wait classified: " + classify_wait_result(wGc, wGcErr));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

#ifdef REMEDY_TEST_WORKER_TREE_SEAM
    uint32_t limit_flags = 0;
    uint32_t active_procs = 0;
    if (!remedy_test_worker_job_get_policy(ctx.token, &limit_flags, &active_procs)) {
        ctx.record_error("remedy_test_worker_job_get_policy seam query failed");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
    if ((limit_flags & JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE) == 0) {
        ctx.record_error("JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE flag missing");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
    if ((limit_flags & JOB_OBJECT_LIMIT_BREAKAWAY_OK) != 0) {
        ctx.record_error("JOB_OBJECT_LIMIT_BREAKAWAY_OK flag unexpectedly present");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
    if ((limit_flags & JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK) != 0) {
        ctx.record_error("JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK flag unexpectedly present");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
    if (active_procs < 3) {
        ctx.record_error("Active processes in Job Object < 3");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    bool c_root = false, c_child = false, c_grandchild = false;
    if (!remedy_test_worker_job_contains_pid(ctx.token, ctx.root_pid, &c_root) || !c_root) {
        ctx.record_error("Job Object does not contain root PID");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
    if (!remedy_test_worker_job_contains_pid(ctx.token, ctx.child_pid, &c_child) || !c_child) {
        ctx.record_error("Job Object does not contain child PID");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
    if (!remedy_test_worker_job_contains_pid(ctx.token, ctx.grandchild_pid, &c_grandchild) || !c_grandchild) {
        ctx.record_error("Job Object does not contain grandchild PID");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
#endif

    remedy_err_t term_err = worker_port_terminate(ctx.token);
    if (term_err != REMEDY_OK) {
        ctx.record_error("worker_port_terminate failed: " + std::to_string(term_err));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
    ctx.job_termination_succeeded = true;

    bool died = false;
    remedy_err_t wait_err = worker_port_wait_for_death(ctx.token, 2000, &died);
    if (wait_err != REMEDY_OK || !died) {
        ctx.record_error("worker_port_wait_for_death did not observe root death");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
    ctx.root_death_verified = true;

    DWORD wChildExit = WaitForSingleObject(ctx.hChild, 2000);
    DWORD wChildExitErr = (wChildExit == WAIT_FAILED) ? GetLastError() : 0;
    if (wChildExit != WAIT_OBJECT_0) {
        ctx.record_error("Child process post-terminate wait classified: " + classify_wait_result(wChildExit, wChildExitErr));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    DWORD wGcExit = WaitForSingleObject(ctx.hGrandchild, 2000);
    DWORD wGcExitErr = (wGcExit == WAIT_FAILED) ? GetLastError() : 0;
    if (wGcExit != WAIT_OBJECT_0) {
        ctx.record_error("Grandchild process post-terminate wait classified: " + classify_wait_result(wGcExit, wGcExitErr));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    remedy_worker_token_t retired_token = ctx.token;
    remedy_err_t des_err = worker_port_destroy(ctx.token);
    if (des_err != REMEDY_OK) {
        ctx.record_error("worker_port_destroy failed: " + std::to_string(des_err));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
    ctx.token = REMEDY_INVALID_WORKER_TOKEN;

    bool test_died = true;
    remedy_err_t r_wait = worker_port_wait_for_death(retired_token, 100, &test_died);
    if (r_wait != REMEDY_ERR_INVALID_ARGUMENT || test_died) {
        ctx.record_error("worker_port_wait_for_death on retired token failed");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
    if (worker_port_terminate(retired_token) != REMEDY_ERR_INVALID_ARGUMENT) {
        ctx.record_error("worker_port_terminate on retired token failed");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
    if (worker_port_destroy(retired_token) != REMEDY_ERR_INVALID_ARGUMENT) {
        ctx.record_error("worker_port_destroy on retired token failed");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    bool clean_ok = ctx.execute_cleanup(test_dir);
    out_primary_err = ctx.first_error;
    out_cleanup_err = ctx.cleanup_errors;
    return clean_ok;
}

static bool run_scenario_b(const char* fixture_exe, std::string& out_primary_err, std::string& out_cleanup_err) {
    test_scenario_context ctx;

    fs::path test_dir = fs::temp_directory_path() / (
        "remedy_test_tree_scen_b_p" + std::to_string(GetCurrentProcessId())
    );
    std::string cd_diag;
    if (!create_dir_clean_checked(test_dir, cd_diag)) {
        out_primary_err = "Directory preparation failed: " + cd_diag;
        ctx.execute_cleanup(test_dir);
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
    std::string test_dir_str = test_dir.string();

    fs::path mode_path = test_dir / "remedy_fixture_tree_mode.tmp";
    const char mode_content[] = "ROOT_EXIT_EARLY";
    atomic_publish_result pub = atomic_publish_file(
        (test_dir / "mode.tmp").c_str(),
        mode_path.c_str(),
        mode_content,
        sizeof(mode_content) - 1
    );
    if (!pub.success) {
        ctx.record_error("Failed to publish mode file: " + pub.diagnostic);
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    remedy_worker_config_t config = { 0 };
    config.executable_path = fixture_exe;
    config.working_directory = test_dir_str.c_str();

    remedy_err_t start_err = worker_port_start(&config, &ctx.token);
    if (start_err != REMEDY_OK || ctx.token == REMEDY_INVALID_WORKER_TOKEN) {
        ctx.record_error("worker_port_start failed: " + std::to_string(start_err));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    fs::path normal_manifest = test_dir / "remedy_tree_manifest.ready";
    fs::path emergency_manifest = test_dir / "remedy_breakaway_emergency.ready";

    auto start_time = std::chrono::steady_clock::now();
    while (std::chrono::steady_clock::now() - start_time < std::chrono::milliseconds(5000)) {
        std::error_code ec1, ec2;
        bool norm_e = fs::exists(normal_manifest, ec1);
        bool emer_e = fs::exists(emergency_manifest, ec2);
        if (ec1 || ec2) {
            ctx.record_error("Polling filesystem query error: " + (ec1 ? ec1.message() : ec2.message()));
            ctx.execute_cleanup(test_dir);
            out_primary_err = ctx.first_error;
            out_cleanup_err = ctx.cleanup_errors;
            return false;
        }
        if (norm_e || emer_e) break;
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }

    std::error_code ec_norm, ec_emer;
    bool normal_found = fs::exists(normal_manifest, ec_norm);
    bool emergency_found = fs::exists(emergency_manifest, ec_emer);
    if (ec_norm || ec_emer) {
        ctx.record_error("Post-polling filesystem query error: " + (ec_norm ? ec_norm.message() : ec_emer.message()));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    if (normal_found && emergency_found) {
        ctx.record_error("Both normal and emergency manifests exist simultaneously");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    if (!normal_found && !emergency_found) {
        ctx.record_error("Neither normal nor emergency manifest appeared within timeout");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    manifest_data mdata;
    if (emergency_found) {
        if (!parse_manifest_file(emergency_manifest, true, mdata)) {
            ctx.record_error("Failed to parse emergency manifest file");
            ctx.execute_cleanup(test_dir);
            out_primary_err = ctx.first_error;
            out_cleanup_err = ctx.cleanup_errors;
            return false;
        }

        ctx.root_pid = mdata.root_pid;
        ctx.child_pid = mdata.child_pid;
        ctx.grandchild_pid = mdata.grandchild_pid;
        ctx.breakaway_pid = mdata.breakaway_pid;

        ctx.hRoot = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, ctx.root_pid);
        if (ctx.hRoot == NULL) {
            ctx.record_cleanup_error("Emergency OpenProcess failed for root PID " + std::to_string(ctx.root_pid) + ", error: " + std::to_string(GetLastError()));
        }

        ctx.hChild = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, ctx.child_pid);
        if (ctx.hChild == NULL) {
            ctx.record_cleanup_error("Emergency OpenProcess failed for child PID " + std::to_string(ctx.child_pid) + ", error: " + std::to_string(GetLastError()));
        }

        ctx.hGrandchild = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, ctx.grandchild_pid);
        if (ctx.hGrandchild == NULL) {
            ctx.record_cleanup_error("Emergency OpenProcess failed for grandchild PID " + std::to_string(ctx.grandchild_pid) + ", error: " + std::to_string(GetLastError()));
        }

        ctx.record_error("Emergency breakaway creation detected (BREAKAWAY_CREATED=1)");

        bool death_verified = (mdata.breakaway_wait_result == WAIT_OBJECT_0);
        if (!death_verified) {
            ctx.hBreakaway = OpenProcess(PROCESS_TERMINATE | SYNCHRONIZE, FALSE, ctx.breakaway_pid);
            if (ctx.hBreakaway == NULL) {
                ctx.record_error("OpenProcess failed for unverified breakaway PID " + std::to_string(ctx.breakaway_pid) + ": " + std::to_string(GetLastError()));
            }
        }

        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    // Normal manifest path
    if (!parse_manifest_file(normal_manifest, false, mdata)) {
        ctx.record_error("Failed to parse normal manifest file");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    ctx.root_pid = mdata.root_pid;
    ctx.child_pid = mdata.child_pid;
    ctx.grandchild_pid = mdata.grandchild_pid;
    ctx.breakaway_pid = mdata.breakaway_pid;

    ctx.hRoot = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, ctx.root_pid);
    if (!ctx.hRoot) {
        ctx.record_error("Failed to open hRoot (PID " + std::to_string(ctx.root_pid) + "): " + std::to_string(GetLastError()));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
    ctx.hChild = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, ctx.child_pid);
    if (!ctx.hChild) {
        ctx.record_error("Failed to open hChild (PID " + std::to_string(ctx.child_pid) + "): " + std::to_string(GetLastError()));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
    ctx.hGrandchild = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, ctx.grandchild_pid);
    if (!ctx.hGrandchild) {
        ctx.record_error("Failed to open hGrandchild (PID " + std::to_string(ctx.grandchild_pid) + "): " + std::to_string(GetLastError()));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    DWORD wRoot = WaitForSingleObject(ctx.hRoot, 0);
    DWORD wRootErr = (wRoot == WAIT_FAILED) ? GetLastError() : 0;
    if (wRoot != WAIT_TIMEOUT) {
        ctx.record_error("Root process initial liveness wait classified: " + classify_wait_result(wRoot, wRootErr));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    DWORD wChild = WaitForSingleObject(ctx.hChild, 0);
    DWORD wChildErr = (wChild == WAIT_FAILED) ? GetLastError() : 0;
    if (wChild != WAIT_TIMEOUT) {
        ctx.record_error("Child process initial liveness wait classified: " + classify_wait_result(wChild, wChildErr));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    DWORD wGc = WaitForSingleObject(ctx.hGrandchild, 0);
    DWORD wGcErr = (wGc == WAIT_FAILED) ? GetLastError() : 0;
    if (wGc != WAIT_TIMEOUT) {
        ctx.record_error("Grandchild process initial liveness wait classified: " + classify_wait_result(wGc, wGcErr));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

#ifdef REMEDY_TEST_WORKER_TREE_SEAM
    bool c_root_pre = false, c_child_pre = false, c_grandchild_pre = false;
    if (!remedy_test_worker_job_contains_pid(ctx.token, ctx.root_pid, &c_root_pre) || !c_root_pre) {
        ctx.record_error("Job Object does not contain root PID before root exit release");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
    if (!remedy_test_worker_job_contains_pid(ctx.token, ctx.child_pid, &c_child_pre) || !c_child_pre) {
        ctx.record_error("Job Object does not contain child PID before root exit release");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
    if (!remedy_test_worker_job_contains_pid(ctx.token, ctx.grandchild_pid, &c_grandchild_pre) || !c_grandchild_pre) {
        ctx.record_error("Job Object does not contain grandchild PID before root exit release");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
#endif

    // Publish release marker AFTER root, child, and grandchild membership checks (Correction 3)
    fs::path release_tmp = test_dir / "remedy_root_exit.tmp";
    fs::path release_marker = test_dir / "remedy_root_exit.release";
    const char release_content[] = "RELEASE";
    atomic_publish_result r_pub = atomic_publish_file(release_tmp.c_str(), release_marker.c_str(), release_content, sizeof(release_content) - 1);
    if (!r_pub.success) {
        ctx.record_error("Failed to publish release marker: " + r_pub.diagnostic);
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    // Wait for root exit
    DWORD wRootExit = WaitForSingleObject(ctx.hRoot, 3000);
    DWORD wRootExitErr = (wRootExit == WAIT_FAILED) ? GetLastError() : 0;
    if (wRootExit != WAIT_OBJECT_0) {
        ctx.record_error("Root process post-release exit wait classified: " + classify_wait_result(wRootExit, wRootExitErr));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    bool died = false;
    remedy_err_t wait_err = worker_port_wait_for_death(ctx.token, 100, &died);
    if (wait_err != REMEDY_OK || !died) {
        ctx.record_error("worker_port_wait_for_death did not report root death after root exit");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
    ctx.root_death_verified = true;

    // Child and grandchild remain alive
    DWORD wChildSurv = WaitForSingleObject(ctx.hChild, 0);
    DWORD wChildSurvErr = (wChildSurv == WAIT_FAILED) ? GetLastError() : 0;
    if (wChildSurv != WAIT_TIMEOUT) {
        ctx.record_error("Child process survival wait classified: " + classify_wait_result(wChildSurv, wChildSurvErr));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    DWORD wGcSurv = WaitForSingleObject(ctx.hGrandchild, 0);
    DWORD wGcSurvErr = (wGcSurv == WAIT_FAILED) ? GetLastError() : 0;
    if (wGcSurv != WAIT_TIMEOUT) {
        ctx.record_error("Grandchild process survival wait classified: " + classify_wait_result(wGcSurv, wGcSurvErr));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

#ifdef REMEDY_TEST_WORKER_TREE_SEAM
    bool c_child_post = false, c_grandchild_post = false;
    if (!remedy_test_worker_job_contains_pid(ctx.token, ctx.child_pid, &c_child_post) || !c_child_post) {
        ctx.record_error("Job Object lost child PID after root exit");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
    if (!remedy_test_worker_job_contains_pid(ctx.token, ctx.grandchild_pid, &c_grandchild_post) || !c_grandchild_post) {
        ctx.record_error("Job Object lost grandchild PID after root exit");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
#endif

    // Pre-terminate destroy rejection
    remedy_err_t pre_des = worker_port_destroy(ctx.token);
    if (pre_des != REMEDY_ERR_INVALID_ARGUMENT) {
        ctx.record_error("worker_port_destroy before terminate did not return REMEDY_ERR_INVALID_ARGUMENT");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    // Terminate Job
    remedy_err_t term_err = worker_port_terminate(ctx.token);
    if (term_err != REMEDY_OK) {
        ctx.record_error("worker_port_terminate failed: " + std::to_string(term_err));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
    ctx.job_termination_succeeded = true;

    DWORD wChildTerm = WaitForSingleObject(ctx.hChild, 2000);
    DWORD wChildTermErr = (wChildTerm == WAIT_FAILED) ? GetLastError() : 0;
    if (wChildTerm != WAIT_OBJECT_0) {
        ctx.record_error("Child process post-terminate wait classified: " + classify_wait_result(wChildTerm, wChildTermErr));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    DWORD wGcTerm = WaitForSingleObject(ctx.hGrandchild, 2000);
    DWORD wGcTermErr = (wGcTerm == WAIT_FAILED) ? GetLastError() : 0;
    if (wGcTerm != WAIT_OBJECT_0) {
        ctx.record_error("Grandchild process post-terminate wait classified: " + classify_wait_result(wGcTerm, wGcTermErr));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

    remedy_worker_token_t retired_token = ctx.token;
    remedy_err_t des_err = worker_port_destroy(ctx.token);
    if (des_err != REMEDY_OK) {
        ctx.record_error("worker_port_destroy failed: " + std::to_string(des_err));
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
    ctx.token = REMEDY_INVALID_WORKER_TOKEN;

    bool test_died = true;
    remedy_err_t r_wait = worker_port_wait_for_death(retired_token, 100, &test_died);
    if (r_wait != REMEDY_ERR_INVALID_ARGUMENT || test_died) {
        ctx.record_error("worker_port_wait_for_death on retired token failed");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
    if (worker_port_terminate(retired_token) != REMEDY_ERR_INVALID_ARGUMENT) {
        ctx.record_error("worker_port_terminate on retired token failed");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
    if (worker_port_destroy(retired_token) != REMEDY_ERR_INVALID_ARGUMENT) {
        ctx.record_error("worker_port_destroy on retired token failed");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }

#ifdef REMEDY_TEST_WORKER_TREE_SEAM
    uint32_t flags = 0, procs = 0;
    if (remedy_test_worker_job_get_policy(retired_token, &flags, &procs)) {
        ctx.record_error("remedy_test_worker_job_get_policy unexpectedly succeeded on retired token");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
    bool contains = true;
    if (remedy_test_worker_job_contains_pid(retired_token, ctx.child_pid, &contains) || contains) {
        ctx.record_error("remedy_test_worker_job_contains_pid unexpectedly succeeded on retired token");
        ctx.execute_cleanup(test_dir);
        out_primary_err = ctx.first_error;
        out_cleanup_err = ctx.cleanup_errors;
        return false;
    }
#endif

    bool clean_ok = ctx.execute_cleanup(test_dir);
    out_primary_err = ctx.first_error;
    out_cleanup_err = ctx.cleanup_errors;
    return clean_ok;
}

int main(int argc, char* argv[]) {
    if (argc < 2) {
        fprintf(stderr, "Usage: %s <path_to_remedy_worker_fixture.exe>\n", argv[0]);
        return 1;
    }

    const char* fixture_exe = argv[1];

#ifdef REMEDY_TEST_WORKER_TREE_SEAM
    // Invalid token seam assertions
    {
        uint32_t limit_flags = 0x1234;
        uint32_t active_procs = 0x5678;
        bool seam_policy_ok = remedy_test_worker_job_get_policy(REMEDY_INVALID_WORKER_TOKEN, &limit_flags, &active_procs);
        if (seam_policy_ok || limit_flags != 0 || active_procs != 0) {
            fprintf(stderr, "Invalid token seam policy check failed.\n");
            return 1;
        }

        bool contains_flag = true;
        bool seam_contains_ok = remedy_test_worker_job_contains_pid(REMEDY_INVALID_WORKER_TOKEN, 1234, &contains_flag);
        if (seam_contains_ok || contains_flag) {
            fprintf(stderr, "Invalid token seam contains check failed.\n");
            return 1;
        }
    }
#endif

    // Scenario A warm-up (loader & runtime handle initialization)
    std::string warmup_primary;
    std::string warmup_cleanup;
    bool warmup_ok = run_scenario_a(fixture_exe, warmup_primary, warmup_cleanup);
    if (!warmup_ok) {
        fprintf(stderr, "Containment warm-up failed. Primary error: %s; Cleanup error: %s\n",
            warmup_primary.c_str(), warmup_cleanup.c_str());
        return 2;
    }

    // Baseline handle count after warm-up
    DWORD baseline_handles = 0;
    BOOL ghc_ok = GetProcessHandleCount(GetCurrentProcess(), &baseline_handles);
    if (!ghc_ok) {
        fprintf(stderr, "GetProcessHandleCount failed for baseline.\n");
        return 3;
    }

    // Scenario A (Pass 1 - executable evidence)
    std::string scen_a_primary, scen_a_cleanup;
    bool scenario_a_ok = run_scenario_a(fixture_exe, scen_a_primary, scen_a_cleanup);
    if (!scenario_a_ok) {
        fprintf(stderr, "Scenario A failed. Primary error: %s; Cleanup error: %s\n",
            scen_a_primary.c_str(), scen_a_cleanup.c_str());
        return 4;
    }

    // Scenario B (Pass 2 - root exit evidence)
    std::string scen_b_primary, scen_b_cleanup;
    bool scenario_b_ok = run_scenario_b(fixture_exe, scen_b_primary, scen_b_cleanup);
    if (!scenario_b_ok) {
        fprintf(stderr, "Scenario B failed. Primary error: %s; Cleanup error: %s\n",
            scen_b_primary.c_str(), scen_b_cleanup.c_str());
        return 5;
    }

    // Baseline handle count verification
    DWORD final_handles = 0;
    ghc_ok = GetProcessHandleCount(GetCurrentProcess(), &final_handles);
    if (!ghc_ok) {
        fprintf(stderr, "GetProcessHandleCount failed for final verification.\n");
        return 6;
    }

    if (final_handles != baseline_handles) {
        fprintf(stderr, "Handle count mismatch: baseline=%lu, final=%lu\n", baseline_handles, final_handles);
        return 7;
    }

    printf("test_worker_tree_containment passed successfully.\n");
    return 0;
}

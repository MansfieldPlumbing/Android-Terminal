#include "domain.h"
#include "object_table.h"
#include "core_objects.h"
#include "remedy/ports/worker_port.h"
#include "remedy/ports/channel_port.h"
#include "remedy/wire_frame.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <iostream>
#include <thread>
#include <vector>
#include <cassert>
#include <stdio.h>
#include <string.h>

static remedy_err_t run_cooperative_quiesce_test(remedy::object_table& table, const char* worker_exe) {
    std::cout << "\n[TEST CASE 1] Cooperative Worker Quiesce (" << worker_exe << ")..." << std::endl;

    remedy_handle_t domain_h = REMEDY_INVALID_HANDLE;
    remedy::domain* d = nullptr;
    assert(remedy::domain::create(&table, &domain_h, &d) == REMEDY_OK);

    remedy_channel_config_t chan_cfg = { "coop_nonce_01", true };
    remedy_channel_token_t c_tok = REMEDY_INVALID_CHANNEL_TOKEN;
    assert(channel_port_create(&chan_cfg, &c_tok) == REMEDY_OK);

    remedy_handle_t c_h = table.insert(REMEDY_OBJECT_CHANNEL, domain_h, new remedy::channel_object(c_tok));
    d->attach_channel(c_tok, c_h);

    remedy_worker_config_t w_cfg = { worker_exe, nullptr, nullptr, "coop_nonce_01", 2000 };
    remedy_worker_token_t w_tok = REMEDY_INVALID_WORKER_TOKEN;
    assert(worker_port_start(&w_cfg, &w_tok) == REMEDY_OK);

    remedy_handle_t w_h = table.insert(REMEDY_OBJECT_WORKER, domain_h, new remedy::worker_object(w_tok));
    d->attach_worker(w_tok, w_h);

    assert(channel_port_connect(c_tok, 3000) == REMEDY_OK);

    // Perform Collapse (sends QUIESCE, receives QUIESCE_ACK, worker exits cleanly)
    remedy_err_t col_res = d->collapse(2000);
    assert(col_res == REMEDY_OK);
    assert(d->state() == REMEDY_DOMAIN_DEAD);
    assert(!table.is_valid(w_h));
    assert(!table.is_valid(c_h));

    table.remove(domain_h);

    std::cout << "[TEST CASE 1] Cooperative Worker Quiesce PASSED!" << std::endl;
    return REMEDY_OK;
}

static remedy_err_t run_hostile_ignore_quiesce_test(remedy::object_table& table, const char* worker_exe) {
    std::cout << "\n[TEST CASE 2] Hostile Worker Ignore Quiesce (Forced Job Kill) (" << worker_exe << ")..." << std::endl;

    remedy_handle_t domain_h = REMEDY_INVALID_HANDLE;
    remedy::domain* d = nullptr;
    assert(remedy::domain::create(&table, &domain_h, &d) == REMEDY_OK);

    remedy_channel_config_t chan_cfg = { "hostile_nonce_02", true };
    remedy_channel_token_t c_tok = REMEDY_INVALID_CHANNEL_TOKEN;
    assert(channel_port_create(&chan_cfg, &c_tok) == REMEDY_OK);

    remedy_handle_t c_h = table.insert(REMEDY_OBJECT_CHANNEL, domain_h, new remedy::channel_object(c_tok));
    d->attach_channel(c_tok, c_h);

    remedy_worker_config_t w_cfg = { worker_exe, "--ignore-quiesce", nullptr, "hostile_nonce_02", 2000 };
    remedy_worker_token_t w_tok = REMEDY_INVALID_WORKER_TOKEN;
    assert(worker_port_start(&w_cfg, &w_tok) == REMEDY_OK);

    remedy_handle_t w_h = table.insert(REMEDY_OBJECT_WORKER, domain_h, new remedy::worker_object(w_tok));
    d->attach_worker(w_tok, w_h);

    assert(channel_port_connect(c_tok, 3000) == REMEDY_OK);

    // Send request setting ignore_quiesce mode
    remedy_wire_frame_header_t req = { 0 };
    req.kind = REMEDY_WIRE_KIND_REQUEST;
    req.request_id = 2001;
    req.domain_handle = domain_h;
    const char* payload = "ignore_quiesce";
    req.payload_len = (uint32_t)strlen(payload);
    assert(channel_port_send_frame(c_tok, &req, payload) == REMEDY_OK);

    remedy_wire_frame_header_t resp = { 0 };
    char resp_buf[256] = { 0 };
    assert(channel_port_read_frame(c_tok, &resp, resp_buf, sizeof(resp_buf)) == REMEDY_OK);

    // Collapse: Quiesce timeout -> Job Object forced kill terminates process
    remedy_err_t col_res = d->collapse(500);
    assert(col_res == REMEDY_OK);
    assert(d->state() == REMEDY_DOMAIN_DEAD);
    assert(!table.is_valid(w_h));

    table.remove(domain_h);

    std::cout << "[TEST CASE 2] Hostile Worker Ignore Quiesce PASSED!" << std::endl;
    return REMEDY_OK;
}

static remedy_err_t run_descendant_process_tree_kill_test(remedy::object_table& table, const char* worker_exe) {
    std::cout << "\n[TEST CASE 3] Descendant Process Tree Cleanup & Verification (" << worker_exe << ")..." << std::endl;

    remedy_handle_t domain_h = REMEDY_INVALID_HANDLE;
    remedy::domain* d = nullptr;
    assert(remedy::domain::create(&table, &domain_h, &d) == REMEDY_OK);

    remedy_channel_config_t chan_cfg = { "desc_nonce_03", true };
    remedy_channel_token_t c_tok = REMEDY_INVALID_CHANNEL_TOKEN;
    assert(channel_port_create(&chan_cfg, &c_tok) == REMEDY_OK);

    remedy_handle_t c_h = table.insert(REMEDY_OBJECT_CHANNEL, domain_h, new remedy::channel_object(c_tok));
    d->attach_channel(c_tok, c_h);

    remedy_worker_config_t w_cfg = { worker_exe, nullptr, nullptr, "desc_nonce_03", 2000 };
    remedy_worker_token_t w_tok = REMEDY_INVALID_WORKER_TOKEN;
    assert(worker_port_start(&w_cfg, &w_tok) == REMEDY_OK);

    remedy_handle_t w_h = table.insert(REMEDY_OBJECT_WORKER, domain_h, new remedy::worker_object(w_tok));
    d->attach_worker(w_tok, w_h);

    assert(channel_port_connect(c_tok, 3000) == REMEDY_OK);

    // Tell worker to spawn a child cmd.exe process and return its PID
    remedy_wire_frame_header_t req = { 0 };
    req.kind = REMEDY_WIRE_KIND_REQUEST;
    req.request_id = 3001;
    req.domain_handle = domain_h;
    const char* payload = "spawn_child";
    req.payload_len = (uint32_t)strlen(payload);
    assert(channel_port_send_frame(c_tok, &req, payload) == REMEDY_OK);

    remedy_wire_frame_header_t resp = { 0 };
    char resp_buf[256] = { 0 };
    assert(channel_port_read_frame(c_tok, &resp, resp_buf, sizeof(resp_buf)) == REMEDY_OK);

    // Parse child PID from response payload "echo_reply:child_pid=XXXX"
    DWORD child_pid = 0;
    char* pid_str = strstr(resp_buf, "child_pid=");
    if (pid_str) {
        child_pid = (DWORD)atoi(pid_str + 10);
    }
    assert(child_pid > 0);
    std::cout << "[TEST CASE 3] Spawned Descendant Process PID: " << child_pid << std::endl;

    HANDLE hChildProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, child_pid);
    assert(hChildProc != NULL);

    // Collapse domain -> Job Object terminates worker AND descendant process tree!
    remedy_err_t col_res = d->collapse(1000);
    assert(col_res == REMEDY_OK);
    assert(d->state() == REMEDY_DOMAIN_DEAD);

    // Verify descendant PID is terminally dead!
    DWORD exitCode = 0;
    BOOL exitOk = GetExitCodeProcess(hChildProc, &exitCode);
    assert(exitOk && exitCode != 259); // 259 == STILL_ACTIVE
    CloseHandle(hChildProc);

    table.remove(domain_h);

    std::cout << "[TEST CASE 3] Descendant Process Tree Cleanup & Verification PASSED! (PID " << child_pid << " Exited)" << std::endl;
    return REMEDY_OK;
}

static remedy_err_t run_late_completion_rejection_test(remedy::object_table& table, const char* worker_exe) {
    std::cout << "\n[TEST CASE 4] Late Completion Dispatcher Guard (" << worker_exe << ")..." << std::endl;

    remedy_handle_t domain_h = REMEDY_INVALID_HANDLE;
    remedy::domain* d = nullptr;
    assert(remedy::domain::create(&table, &domain_h, &d) == REMEDY_OK);

    // Collapse domain immediately
    d->collapse(500);
    assert(d->state() == REMEDY_DOMAIN_DEAD);

    // Attempt processing late completion
    remedy_wire_frame_header_t late_hdr = { 0 };
    late_hdr.kind = REMEDY_WIRE_KIND_COMPLETION;
    late_hdr.request_id = 4001;
    late_hdr.domain_handle = domain_h;

    remedy_err_t res = d->validate_and_process_completion(4001, &late_hdr);
    assert(res == REMEDY_ERR_LATE_COMPLETION);

    table.remove(domain_h);

    std::cout << "[TEST CASE 4] Late Completion Dispatcher Guard PASSED!" << std::endl;
    return REMEDY_OK;
}

int main(int argc, char* argv[]) {
    const char* worker_exe = "remedy_echo_worker.exe";
    if (argc > 1) {
        worker_exe = argv[1];
    }

    std::cout << "[TEST] Starting Comprehensive Conformance Suite for Target Worker: " << worker_exe << std::endl;

    remedy::object_table table;

    run_cooperative_quiesce_test(table, worker_exe);
    run_hostile_ignore_quiesce_test(table, worker_exe);
    run_descendant_process_tree_kill_test(table, worker_exe);
    run_late_completion_rejection_test(table, worker_exe);

    std::cout << "\n[TEST] Comprehensive Conformance Suite for " << worker_exe << " PASSED!" << std::endl;
    return 0;
}

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include "remedy/receipt_check.h"
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include <string>
#include <unordered_map>

#include "terminal/router_client.h"

static DWORD process_handle_count() {
    DWORD count = 0;
    REMEDY_RECEIPT_CHECK(GetProcessHandleCount(GetCurrentProcess(), &count));
    return count;
}

static void warm_process_creation() {
    std::wstring command = L"C:\\Windows\\System32\\cmd.exe /d /c exit";
    STARTUPINFOW startup{sizeof(startup)};
    PROCESS_INFORMATION process{};
    REMEDY_RECEIPT_CHECK(CreateProcessW(nullptr, command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW,
                          nullptr, nullptr, &startup, &process));
    REMEDY_RECEIPT_CHECK(WaitForSingleObject(process.hProcess, 5000) == WAIT_OBJECT_0);
    REMEDY_RECEIPT_CHECK(CloseHandle(process.hThread));
    REMEDY_RECEIPT_CHECK(CloseHandle(process.hProcess));
}

struct route_info {
    uint64_t id{0};
    uint32_t pid{0};
};

static terminal_router_message_t read_message(
    terminal_router_client_t* client,
    std::unordered_map<uint64_t, std::string>& output) {
    terminal_router_message_t message{};
    remedy_err_t read_result = terminal_router_client_read(client, &message);
    REMEDY_RECEIPT_CHECK(read_result == REMEDY_OK);
    if (message.header.operation == TERMINAL_ROUTER_OUTPUT) {
        output[message.header.route_id].append(
            reinterpret_cast<const char*>(message.payload),
            message.header.payload_len);
    }
    return message;
}

static route_info open_route(
    terminal_router_client_t* client,
    const std::string& command,
    std::unordered_map<uint64_t, std::string>& output) {
    uint64_t correlation = 0;
    REMEDY_RECEIPT_CHECK(terminal_router_client_send(
        client, TERMINAL_ROUTER_OPEN, 0, 100, 30,
        command.data(), command.size(), &correlation) == REMEDY_OK);
    for (;;) {
        terminal_router_message_t message = read_message(client, output);
        if (message.header.operation == TERMINAL_ROUTER_OPENED) {
            REMEDY_RECEIPT_CHECK(message.correlation == correlation);
            REMEDY_RECEIPT_CHECK(message.header.route_id != 0);
            REMEDY_RECEIPT_CHECK(message.header.value_a != 0);
            return {message.header.route_id, message.header.value_a};
        }
        REMEDY_RECEIPT_CHECK(message.header.operation == TERMINAL_ROUTER_OUTPUT);
    }
}

static void send_input(terminal_router_client_t* client, uint64_t route, const char* text) {
    uint64_t correlation = 0;
    REMEDY_RECEIPT_CHECK(terminal_router_client_send(
        client, TERMINAL_ROUTER_INPUT, route, 0, 0,
        text, strlen(text), &correlation) == REMEDY_OK);
    REMEDY_RECEIPT_CHECK(correlation != 0);
}

static void read_until(
    terminal_router_client_t* client,
    std::unordered_map<uint64_t, std::string>& output,
    uint64_t route,
    const char* marker) {
    while (output[route].find(marker) == std::string::npos) {
        terminal_router_message_t message = read_message(client, output);
        REMEDY_RECEIPT_CHECK(message.header.operation == TERMINAL_ROUTER_OUTPUT);
        REMEDY_RECEIPT_CHECK(message.header.route_id != 0);
    }
}

static void wait_for_route_ready(
    terminal_router_client_t* client,
    std::unordered_map<uint64_t, std::string>& output,
    uint64_t route) {
    while (output[route].empty()) {
        terminal_router_message_t message = read_message(client, output);
        REMEDY_RECEIPT_CHECK(message.header.operation == TERMINAL_ROUTER_OUTPUT);
        REMEDY_RECEIPT_CHECK(message.header.route_id != 0);
    }
}

static void resize_route(
    terminal_router_client_t* client,
    std::unordered_map<uint64_t, std::string>& output,
    uint64_t route) {
    uint64_t correlation = 0;
    REMEDY_RECEIPT_CHECK(terminal_router_client_send(
        client, TERMINAL_ROUTER_RESIZE, route, 132, 44,
        nullptr, 0, &correlation) == REMEDY_OK);
    for (;;) {
        terminal_router_message_t message = read_message(client, output);
        if (message.header.operation == TERMINAL_ROUTER_RESIZED) {
            REMEDY_RECEIPT_CHECK(message.correlation == correlation);
            REMEDY_RECEIPT_CHECK(message.header.route_id == route);
            REMEDY_RECEIPT_CHECK(message.header.value_a == 132);
            REMEDY_RECEIPT_CHECK(message.header.value_b == 44);
            return;
        }
        REMEDY_RECEIPT_CHECK(message.header.operation == TERMINAL_ROUTER_OUTPUT);
    }
}

static void close_route(
    terminal_router_client_t* client,
    std::unordered_map<uint64_t, std::string>& output,
    uint64_t route) {
    uint64_t correlation = 0;
    REMEDY_RECEIPT_CHECK(terminal_router_client_send(
        client, TERMINAL_ROUTER_CLOSE, route, 0, 0,
        nullptr, 0, &correlation) == REMEDY_OK);
    for (;;) {
        terminal_router_message_t message = read_message(client, output);
        if (message.header.operation == TERMINAL_ROUTER_CLOSED) {
            REMEDY_RECEIPT_CHECK(message.correlation == correlation);
            REMEDY_RECEIPT_CHECK(message.header.route_id == route);
            return;
        }
        REMEDY_RECEIPT_CHECK(message.header.operation == TERMINAL_ROUTER_OUTPUT);
    }
}

static void assert_process_dead(uint32_t pid) {
    HANDLE process = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!process) {
        REMEDY_RECEIPT_CHECK(GetLastError() == ERROR_INVALID_PARAMETER);
        return;
    }
    REMEDY_RECEIPT_CHECK(WaitForSingleObject(process, 0) == WAIT_OBJECT_0);
    REMEDY_RECEIPT_CHECK(CloseHandle(process));
}

int main(int argc, char** argv) {
    REMEDY_RECEIPT_CHECK(argc == 3);
    const char* router = argv[1];
    const char* pwsh = argv[2];
    warm_process_creation();
    const DWORD baseline_handles = process_handle_count();

    std::string channel_name =
        "terminal_router_" + std::to_string(GetCurrentProcessId()) + "_" +
        std::to_string(GetTickCount64());
    terminal_router_client_t client{};
    REMEDY_RECEIPT_CHECK(terminal_router_client_start(router, channel_name.c_str(), &client) == REMEDY_OK);
    REMEDY_RECEIPT_CHECK(client.remedy.lifecycle.ready_received);
    REMEDY_RECEIPT_CHECK(client.remedy.lifecycle.state == REMEDY_WORKER_LIFECYCLE_COMPLETED);
    const remedy_worker_token_t stale_worker = client.remedy.lifecycle.worker;
    const remedy_channel_token_t stale_channel = client.remedy.lifecycle.channel;

    std::string command = "\"" + std::string(pwsh) + "\" -NoLogo -NoProfile -NoExit";
    std::unordered_map<uint64_t, std::string> output;
    route_info a = open_route(&client, command, output);
    route_info b = open_route(&client, command, output);
    REMEDY_RECEIPT_CHECK(a.id != b.id);
    REMEDY_RECEIPT_CHECK(a.pid != b.pid);
    wait_for_route_ready(&client, output, a.id);
    wait_for_route_ready(&client, output, b.id);

    send_input(&client, a.id, "[Console]::WriteLine('REMEDY-' + 'A-OUTPUT')\r\n");
    send_input(&client, b.id, "[Console]::WriteLine('REMEDY-' + 'B-OUTPUT')\r\n");
    read_until(&client, output, a.id, "REMEDY-A-OUTPUT");
    read_until(&client, output, b.id, "REMEDY-B-OUTPUT");
    REMEDY_RECEIPT_CHECK(output[a.id].find("REMEDY-B-OUTPUT") == std::string::npos);
    REMEDY_RECEIPT_CHECK(output[b.id].find("REMEDY-A-OUTPUT") == std::string::npos);

    resize_route(&client, output, a.id);
    send_input(&client, b.id, "[Console]::WriteLine('REMEDY-B-' + 'AFTER-RESIZE')\r\n");
    read_until(&client, output, b.id, "REMEDY-B-AFTER-RESIZE");

    close_route(&client, output, a.id);
    assert_process_dead(a.pid);
    send_input(&client, b.id, "[Console]::WriteLine('REMEDY-B-' + 'AFTER-A-CLOSE')\r\n");
    read_until(&client, output, b.id, "REMEDY-B-AFTER-A-CLOSE");
    close_route(&client, output, b.id);
    assert_process_dead(b.pid);

    REMEDY_RECEIPT_CHECK(terminal_router_client_quiesce_and_retire(&client) == REMEDY_OK);
    REMEDY_RECEIPT_CHECK(client.remedy.lifecycle.quiesce_acknowledged);
    REMEDY_RECEIPT_CHECK(client.remedy.lifecycle.worker_exited);
    REMEDY_RECEIPT_CHECK(client.remedy.lifecycle.terminal_channel_observed);
    REMEDY_RECEIPT_CHECK(client.remedy.lifecycle.state == REMEDY_WORKER_LIFECYCLE_RETIRED);
    bool died = false;
    REMEDY_RECEIPT_CHECK(worker_port_wait_for_death(stale_worker, 1, &died) == REMEDY_ERR_INVALID_ARGUMENT);
    REMEDY_RECEIPT_CHECK(channel_port_connect(stale_channel, 1) == REMEDY_ERR_INVALID_ARGUMENT);
    REMEDY_RECEIPT_CHECK(process_handle_count() == baseline_handles);

    puts("Terminal ConPTY router receipt passed: two pwsh routes, crossed-route isolation, resize isolation, independent close, quiesce, and handle restoration.");
    return 0;
}

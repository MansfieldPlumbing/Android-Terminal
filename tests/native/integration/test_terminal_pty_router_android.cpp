#include "remedy/receipt_check.h"
#include <dirent.h>
#include <errno.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

#include <string>
#include <unordered_map>

#include "terminal/router_client.h"

static size_t descriptor_count() {
    DIR* directory = opendir("/proc/self/fd");
    REMEDY_RECEIPT_CHECK(directory != nullptr);
    size_t count = 0;
    for (;;) {
        errno = 0;
        dirent* entry = readdir(directory);
        if (!entry) {
            REMEDY_RECEIPT_CHECK(errno == 0);
            break;
        }
        if (strcmp(entry->d_name, ".") != 0 && strcmp(entry->d_name, "..") != 0) ++count;
    }
    REMEDY_RECEIPT_CHECK(closedir(directory) == 0);
    return count;
}

struct route_info {
    uint64_t id{0};
    uint32_t pid{0};
};

static terminal_router_message_t read_message(
    terminal_router_client_t* client,
    std::unordered_map<uint64_t, std::string>& output) {
    terminal_router_message_t message{};
    REMEDY_RECEIPT_CHECK(terminal_router_client_read(client, &message) == REMEDY_OK);
    if (message.header.operation == TERMINAL_ROUTER_OUTPUT) {
        output[message.header.route_id].append(
            reinterpret_cast<const char*>(message.payload),
            message.header.payload_len);
    }
    return message;
}

static route_info open_route(
    terminal_router_client_t* client,
    const char* executable,
    std::unordered_map<uint64_t, std::string>& output) {
    uint64_t correlation = 0;
    REMEDY_RECEIPT_CHECK(terminal_router_client_send(
        client, TERMINAL_ROUTER_OPEN, 0, 100, 30,
        executable, strlen(executable), &correlation) == REMEDY_OK);
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

static void send_input(terminal_router_client_t* client, uint64_t route, const char* input) {
    uint64_t correlation = 0;
    REMEDY_RECEIPT_CHECK(terminal_router_client_send(
        client, TERMINAL_ROUTER_INPUT, route, 0, 0,
        input, strlen(input), &correlation) == REMEDY_OK);
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
    errno = 0;
    REMEDY_RECEIPT_CHECK(kill(static_cast<pid_t>(pid), 0) == -1);
    REMEDY_RECEIPT_CHECK(errno == ESRCH);
}

int main(int argc, char** argv) {
    REMEDY_RECEIPT_CHECK(argc == 3);
    const char* router = argv[1];
    const char* shell = argv[2];
    const size_t baseline_descriptors = descriptor_count();

    terminal_router_client_t client{};
    REMEDY_RECEIPT_CHECK(terminal_router_client_start(router, "terminal_router_android", &client) == REMEDY_OK);
    REMEDY_RECEIPT_CHECK(client.remedy.lifecycle.ready_received);
    REMEDY_RECEIPT_CHECK(client.remedy.lifecycle.state == REMEDY_WORKER_LIFECYCLE_COMPLETED);
    const remedy_worker_token_t stale_worker = client.remedy.lifecycle.worker;
    const remedy_channel_token_t stale_channel = client.remedy.lifecycle.channel;

    std::unordered_map<uint64_t, std::string> output;
    route_info a = open_route(&client, shell, output);
    route_info b = open_route(&client, shell, output);
    REMEDY_RECEIPT_CHECK(a.id != b.id);
    REMEDY_RECEIPT_CHECK(a.pid != b.pid);

    send_input(&client, a.id, "printf 'REMEDY-%s\\n' 'A-OUTPUT'\n");
    send_input(&client, b.id, "printf 'REMEDY-%s\\n' 'B-OUTPUT'\n");
    read_until(&client, output, a.id, "REMEDY-A-OUTPUT");
    read_until(&client, output, b.id, "REMEDY-B-OUTPUT");
    REMEDY_RECEIPT_CHECK(output[a.id].find("REMEDY-B-OUTPUT") == std::string::npos);
    REMEDY_RECEIPT_CHECK(output[b.id].find("REMEDY-A-OUTPUT") == std::string::npos);

    resize_route(&client, output, a.id);
    send_input(&client, b.id, "printf 'REMEDY-B-%s\\n' 'AFTER-RESIZE'\n");
    read_until(&client, output, b.id, "REMEDY-B-AFTER-RESIZE");

    close_route(&client, output, a.id);
    assert_process_dead(a.pid);
    send_input(&client, b.id, "printf 'REMEDY-B-%s\\n' 'AFTER-A-CLOSE'\n");
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
    REMEDY_RECEIPT_CHECK(descriptor_count() == baseline_descriptors);

    puts("Terminal Android PTY router receipt passed: two shell routes, crossed-route isolation, resize isolation, independent close, quiesce, and fd restoration.");
    return 0;
}

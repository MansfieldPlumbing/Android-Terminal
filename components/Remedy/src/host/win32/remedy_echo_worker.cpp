#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include "remedy/types.h"
#include "remedy/wire_frame.h"
#include <stdio.h>
#include <string.h>

static remedy_err_t read_exact(HANDLE hPipe, uint8_t* buffer, size_t count) {
    size_t total = 0;
    while (total < count) {
        DWORD readBytes = 0;
        BOOL ok = ReadFile(hPipe, buffer + total, (DWORD)(count - total), &readBytes, NULL);
        if (!ok || readBytes == 0) return REMEDY_ERR_IPC_FAILURE;
        total += readBytes;
    }
    return REMEDY_OK;
}

static remedy_err_t write_exact(HANDLE hPipe, const uint8_t* buffer, size_t count) {
    size_t total = 0;
    while (total < count) {
        DWORD written = 0;
        BOOL ok = WriteFile(hPipe, buffer + total, (DWORD)(count - total), &written, NULL);
        if (!ok || written == 0) return REMEDY_ERR_IPC_FAILURE;
        total += written;
    }
    return REMEDY_OK;
}

int main(int argc, char* argv[]) {
    char channel_nonce[256] = {0};

    for (int i = 1; i < argc; ++i) {
        if (strncmp(argv[i], "--channel=", 10) == 0) {
            strncpy(channel_nonce, argv[i] + 10, sizeof(channel_nonce) - 1);
        }
    }

    if (channel_nonce[0] == '\0') {
        return 1;
    }

    char pipe_path[256];
    snprintf(pipe_path, sizeof(pipe_path), "\\\\.\\pipe\\remedy-worker-%s", channel_nonce);

    wchar_t wPath[256];
    MultiByteToWideChar(CP_UTF8, 0, pipe_path, -1, wPath, 256);

    HANDLE hPipe = INVALID_HANDLE_VALUE;
    for (int retry = 0; retry < 50; ++retry) {
        hPipe = CreateFileW(wPath, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
        if (hPipe != INVALID_HANDLE_VALUE) break;
        Sleep(50);
    }

    if (hPipe == INVALID_HANDLE_VALUE) return 2;

    bool ignore_quiesce = false;

    while (true) {
        uint8_t header_buf[36];
        if (read_exact(hPipe, header_buf, 36) != REMEDY_OK) break;

        remedy_wire_frame_header_t hdr = {0};
        if (remedy_wire_frame_decode(header_buf, &hdr) != REMEDY_OK) {
            break;
        }

        char payload[1024] = {0};
        if (hdr.payload_len > 0) {
            if (hdr.payload_len >= sizeof(payload) || read_exact(hPipe, (uint8_t*)payload, hdr.payload_len) != REMEDY_OK) {
                break;
            }
            uint32_t actual_chk = remedy_adler32((const uint8_t*)payload, hdr.payload_len);
            if (actual_chk != hdr.checksum) {
                break;
            }
        }

        if (hdr.kind == REMEDY_WIRE_KIND_PING) {
            remedy_wire_frame_header_t reply_hdr = {0};
            reply_hdr.magic = REMEDY_WIRE_MAGIC;
            reply_hdr.version = REMEDY_WIRE_VERSION;
            reply_hdr.kind = REMEDY_WIRE_KIND_PONG;
            reply_hdr.header_len = REMEDY_WIRE_HEADER_SIZE;
            reply_hdr.payload_len = 0;
            reply_hdr.request_id = hdr.request_id;
            reply_hdr.domain_handle = hdr.domain_handle;

            uint8_t reply_buf[36];
            remedy_wire_frame_encode(&reply_hdr, reply_buf);
            write_exact(hPipe, reply_buf, 36);
        } else if (hdr.kind == REMEDY_WIRE_KIND_REQUEST) {
            char reply_msg[256] = "echo_reply";

            if (strstr(payload, "spawn_child")) {
                STARTUPINFOW si = { sizeof(si) };
                PROCESS_INFORMATION pi = { 0 };
                wchar_t cmd[] = L"cmd.exe /c ping 127.0.0.1 -n 30 > NUL";
                if (CreateProcessW(NULL, cmd, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &si, &pi)) {
                    snprintf(reply_msg, sizeof(reply_msg), "echo_reply:child_pid=%u", pi.dwProcessId);
                    CloseHandle(pi.hThread);
                    CloseHandle(pi.hProcess);
                }
            } else if (strstr(payload, "ignore_quiesce")) {
                ignore_quiesce = true;
            } else if (strstr(payload, "late_completion")) {
                Sleep(1500);
            }

            remedy_wire_frame_header_t reply_hdr = {0};
            reply_hdr.magic = REMEDY_WIRE_MAGIC;
            reply_hdr.version = REMEDY_WIRE_VERSION;
            reply_hdr.kind = REMEDY_WIRE_KIND_COMPLETION;
            reply_hdr.header_len = REMEDY_WIRE_HEADER_SIZE;
            reply_hdr.payload_len = (uint32_t)strlen(reply_msg);
            reply_hdr.checksum = remedy_adler32((const uint8_t*)reply_msg, reply_hdr.payload_len);
            reply_hdr.request_id = hdr.request_id;
            reply_hdr.domain_handle = hdr.domain_handle;

            uint8_t reply_buf[36];
            remedy_wire_frame_encode(&reply_hdr, reply_buf);
            write_exact(hPipe, reply_buf, 36);
            write_exact(hPipe, (const uint8_t*)reply_msg, reply_hdr.payload_len);
        } else if (hdr.kind == REMEDY_WIRE_KIND_QUIESCE) {
            if (!ignore_quiesce) {
                remedy_wire_frame_header_t ack_hdr = {0};
                ack_hdr.magic = REMEDY_WIRE_MAGIC;
                ack_hdr.version = REMEDY_WIRE_VERSION;
                ack_hdr.kind = REMEDY_WIRE_KIND_QUIESCE_ACK;
                ack_hdr.header_len = REMEDY_WIRE_HEADER_SIZE;
                ack_hdr.request_id = hdr.request_id;
                ack_hdr.domain_handle = hdr.domain_handle;

                uint8_t ack_buf[36];
                remedy_wire_frame_encode(&ack_hdr, ack_buf);
                write_exact(hPipe, ack_buf, 36);
                break;
            }
        }
    }

    CloseHandle(hPipe);
    return 0;
}

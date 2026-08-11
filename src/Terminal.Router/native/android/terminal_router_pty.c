#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define _WIN32_WINNT 0x0A00
#include <windows.h>
#include <stdint.h>
#include <stdlib.h>

__declspec(dllexport)
int __cdecl terminal_router_attach_conpty(
    void* pseudo_console,
    const wchar_t* command,
    uintptr_t* out_process,
    uintptr_t* out_thread,
    uint32_t* out_pid) {
    if (!pseudo_console || !command || !out_process || !out_thread || !out_pid) return ERROR_INVALID_PARAMETER;
    *out_process = 0;
    *out_thread = 0;
    *out_pid = 0;

    SIZE_T attribute_size = 0;
    InitializeProcThreadAttributeList(NULL, 1, 0, &attribute_size);
    if (attribute_size == 0) return (int)GetLastError();
    PPROC_THREAD_ATTRIBUTE_LIST attributes =
        (PPROC_THREAD_ATTRIBUTE_LIST)HeapAlloc(GetProcessHeap(), 0, attribute_size);
    if (!attributes) return ERROR_NOT_ENOUGH_MEMORY;
    if (!InitializeProcThreadAttributeList(attributes, 1, 0, &attribute_size)) {
        int error = (int)GetLastError();
        HeapFree(GetProcessHeap(), 0, attributes);
        return error;
    }
    if (!UpdateProcThreadAttribute(
            attributes, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
            pseudo_console, sizeof(HPCON), NULL, NULL)) {
        int error = (int)GetLastError();
        DeleteProcThreadAttributeList(attributes);
        HeapFree(GetProcessHeap(), 0, attributes);
        return error;
    }

    size_t command_length = wcslen(command) + 1;
    wchar_t* mutable_command = (wchar_t*)HeapAlloc(
        GetProcessHeap(), 0, command_length * sizeof(wchar_t));
    if (!mutable_command) {
        DeleteProcThreadAttributeList(attributes);
        HeapFree(GetProcessHeap(), 0, attributes);
        return ERROR_NOT_ENOUGH_MEMORY;
    }
    memcpy(mutable_command, command, command_length * sizeof(wchar_t));

    STARTUPINFOEXW startup;
    ZeroMemory(&startup, sizeof(startup));
    startup.StartupInfo.cb = sizeof(startup);
    startup.lpAttributeList = attributes;
    PROCESS_INFORMATION process;
    ZeroMemory(&process, sizeof(process));
    HANDLE saved_input = GetStdHandle(STD_INPUT_HANDLE);
    HANDLE saved_output = GetStdHandle(STD_OUTPUT_HANDLE);
    HANDLE saved_error = GetStdHandle(STD_ERROR_HANDLE);
    if (!SetStdHandle(STD_INPUT_HANDLE, NULL) ||
        !SetStdHandle(STD_OUTPUT_HANDLE, NULL) ||
        !SetStdHandle(STD_ERROR_HANDLE, NULL)) {
        int std_error = (int)GetLastError();
        SetStdHandle(STD_INPUT_HANDLE, saved_input);
        SetStdHandle(STD_OUTPUT_HANDLE, saved_output);
        SetStdHandle(STD_ERROR_HANDLE, saved_error);
        HeapFree(GetProcessHeap(), 0, mutable_command);
        DeleteProcThreadAttributeList(attributes);
        HeapFree(GetProcessHeap(), 0, attributes);
        return std_error;
    }
    BOOL created = CreateProcessW(
        NULL, mutable_command, NULL, NULL, FALSE,
        EXTENDED_STARTUPINFO_PRESENT, NULL, NULL,
        &startup.StartupInfo, &process);
    int error = created ? 0 : (int)GetLastError();
    BOOL restored_input = SetStdHandle(STD_INPUT_HANDLE, saved_input);
    int restore_error = restored_input ? 0 : (int)GetLastError();
    BOOL restored_output = SetStdHandle(STD_OUTPUT_HANDLE, saved_output);
    if (!restored_output && restore_error == 0) restore_error = (int)GetLastError();
    BOOL restored_error = SetStdHandle(STD_ERROR_HANDLE, saved_error);
    if (!restored_error && restore_error == 0) restore_error = (int)GetLastError();
    BOOL restored = restored_input && restored_output && restored_error;
    if (!restored && created) {
        TerminateProcess(process.hProcess, 1);
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        created = FALSE;
        error = restore_error;
    }
    HeapFree(GetProcessHeap(), 0, mutable_command);
    DeleteProcThreadAttributeList(attributes);
    HeapFree(GetProcessHeap(), 0, attributes);
    if (!created) return error;
    *out_process = (uintptr_t)process.hProcess;
    *out_thread = (uintptr_t)process.hThread;
    *out_pid = process.dwProcessId;
    return 0;
}

#else

#include <dlfcn.h>
#include <errno.h>
#include <pty.h>
#include <signal.h>
#include <stdint.h>
#include <stdlib.h>
#include <sys/ioctl.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>

#ifdef TERMINAL_ROUTER_LAUNCHER

typedef int (*terminal_router_main_fn)(void);

int main(void) {
    void* library = dlopen("libterminal-router.so", RTLD_NOW | RTLD_LOCAL);
    if (!library) return 126;
    terminal_router_main_fn run = (terminal_router_main_fn)dlsym(library, "terminal_router_main");
    if (!run) {
        dlclose(library);
        return 127;
    }
    int result = run();
    dlclose(library);
    return result;
}

#else

__attribute__((visibility("default")))
int terminal_router_spawn_pty(
    const char* executable,
    uint16_t columns,
    uint16_t rows,
    int* out_master,
    int* out_pid) {
    if (!executable || executable[0] != '/' || columns == 0 || rows == 0 ||
        !out_master || !out_pid) return EINVAL;
    *out_master = -1;
    *out_pid = -1;
    struct winsize size = {rows, columns, 0, 0};
    int master = -1;
    pid_t pid = forkpty(&master, NULL, NULL, &size);
    if (pid < 0) return errno;
    if (pid == 0) {
        execl(executable, executable, (char*)NULL);
        _exit(127);
    }
    *out_master = master;
    *out_pid = pid;
    return 0;
}

__attribute__((visibility("default")))
int terminal_router_resize_pty(int master, uint16_t columns, uint16_t rows) {
    if (master < 0 || columns == 0 || rows == 0) return EINVAL;
    struct winsize size = {rows, columns, 0, 0};
    return ioctl(master, TIOCSWINSZ, &size) == 0 ? 0 : errno;
}

__attribute__((visibility("default")))
int terminal_router_close_pty(int master, int pid, int* out_exit_code) {
    if (master < 0 || pid <= 0 || !out_exit_code) return EINVAL;
    int close_error = close(master) == 0 ? 0 : errno;
    if (kill(-pid, SIGKILL) != 0 && errno != ESRCH) return errno;
    int status = 0;
    pid_t waited;
    do { waited = waitpid(pid, &status, 0); } while (waited < 0 && errno == EINTR);
    if (waited != pid) return errno == 0 ? ECHILD : errno;
    if (WIFEXITED(status)) *out_exit_code = WEXITSTATUS(status);
    else if (WIFSIGNALED(status)) *out_exit_code = 128 + WTERMSIG(status);
    else return ECHILD;
    return close_error;
}

#endif

#endif

#ifdef __cplusplus
extern "C" {
#endif

// EXPORT macro forces the NDK to keep these in the dynamic symbol table
#define EXPORT __attribute__((visibility("default")))

EXPORT void SysLogProvider_OpenSysLog(const char* identity, int facility) {}
EXPORT void SysLogProvider_CloseSysLog() {}
EXPORT void SysLogProvider_LogSysLog(int priority, const char* message) {}

EXPORT void Native_OpenLog(const char* identity, int facility) {}
EXPORT void Native_CloseLog() {}
EXPORT void Native_LogSysLog(int priority, const char* message) {}
EXPORT void Native_SysLog(int priority, const char* message) {} 

EXPORT int GetCurrentThreadId() { return 1; }

#ifdef __cplusplus
}
#endif

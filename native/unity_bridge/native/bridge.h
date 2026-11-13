#ifndef JSB_BRIDGE_H
#define JSB_BRIDGE_H
#include <windows.h>
#ifdef JSB_BUILD
#define JSB_API __declspec(dllexport)
#else
#define JSB_API __declspec(dllimport)
#endif
#define JSB_PROTOCOL 2
#define JSB_MESSAGE_TAG 0x4a534231UL
#define JSB_NONCE_CHARS 32
enum JSB_STATE { JSB_EMPTY=0, JSB_ARMED=1, JSB_RUNNING=2,
    JSB_COMPLETE=3, JSB_CANCELLED=4, JSB_FAILED=5 };
typedef struct JSB_STATUS {
    DWORD protocol, pid, tid, hwnd32, cookie;
    ULONGLONG expected_creation_filetime;
    volatile LONG state, calls;
    DWORD callback_tid, mono_main_match, win32_error, result_code;
    DWORD hook_removed, retained_until_restart;
    volatile LONG callback_finished;
    char detail[384];
} JSB_STATUS;
/* Fixed ABI: x86 cdecl exports, no arbitrary method/path parameters. */
JSB_API int __cdecl JSB_Arm(HWND hwnd, DWORD pid, DWORD tid, const wchar_t *nonce, ULONGLONG expected_creation_filetime);
JSB_API int __cdecl JSB_Fire(DWORD timeout_ms, JSB_STATUS *status);
JSB_API int __cdecl JSB_Cancel(JSB_STATUS *status);
JSB_API int __cdecl JSB_ReadStatus(JSB_STATUS *status);
JSB_API int __cdecl JSB_Close(void);
JSB_API LRESULT CALLBACK JSB_HookProc(int code, WPARAM wp, LPARAM lp);
#endif

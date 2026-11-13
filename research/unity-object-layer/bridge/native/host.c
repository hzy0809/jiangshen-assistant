#define UNICODE
#define _UNICODE
#include "bridge.h"
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>
typedef int (__cdecl *ARM)(HWND,DWORD,DWORD,const wchar_t*);
typedef int (__cdecl *STATUS)(JSB_STATUS*);
typedef int (__cdecl *FIRE)(DWORD,JSB_STATUS*);
typedef int (__cdecl *CLOSE)(void);
static ARM arm; static STATUS cancel,readstatus; static FIRE fire; static CLOSE closebridge;
typedef struct READY { DWORD hwnd,tid,pid; } READY;
static void print_status(const char *event,JSB_STATUS *s) {
    /* Detail ABI is fixed printable ASCII and contains no free-form exception text. */
    printf("{\"event\":\"%s\",\"protocol\":%lu,\"pid\":%lu,\"tid\":%lu,\"callback_tid\":%lu,\"state\":%ld,\"calls\":%ld,\"mono_main_match\":%lu,\"callback_finished\":%ld,\"hook_removed\":%lu,\"retained_until_restart\":%lu,\"win32_error\":%lu,\"result_code\":%lu,\"detail\":\"%s\"}\n",
        event,(unsigned long)s->protocol,(unsigned long)s->pid,(unsigned long)s->tid,(unsigned long)s->callback_tid,
        (long)s->state,(long)s->calls,(unsigned long)s->mono_main_match,(long)s->callback_finished,
        (unsigned long)s->hook_removed,(unsigned long)s->retained_until_restart,(unsigned long)s->win32_error,
        (unsigned long)s->result_code,s->detail); fflush(stdout);
}
static LRESULT CALLBACK target_proc(HWND h,UINT message,WPARAM w,LPARAM l) {
    if(message==WM_CLOSE) { DestroyWindow(h); return 0; }
    if(message==WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(h,message,w,l);
}
static int child(const wchar_t *name) {
    HANDLE map,event; READY *r; wchar_t eventname[120]; WNDCLASSW cls; HWND window; MSG msg;
    map=OpenFileMappingW(FILE_MAP_ALL_ACCESS,FALSE,name); if(!map) return 20;
    r=(READY*)MapViewOfFile(map,FILE_MAP_ALL_ACCESS,0,0,sizeof(*r)); if(!r) return 21;
    ZeroMemory(&cls,sizeof(cls)); cls.lpfnWndProc=target_proc; cls.hInstance=GetModuleHandleW(NULL); cls.lpszClassName=L"JSBridgeSyntheticWindow";
    if(!RegisterClassW(&cls)) return 22;
    window=CreateWindowW(cls.lpszClassName,L"Synthetic bridge validation",0,0,0,0,0,HWND_MESSAGE,NULL,cls.hInstance,NULL);
    if(!window) return 23;
    r->pid=GetCurrentProcessId(); r->tid=GetCurrentThreadId(); r->hwnd=(DWORD)(ULONG_PTR)window;
    swprintf(eventname,120,L"%ls-ready",name); event=OpenEventW(EVENT_MODIFY_STATE,FALSE,eventname);
    if(!event) return 24; SetEvent(event); CloseHandle(event);
    while(GetMessageW(&msg,NULL,0,0)>0) { TranslateMessage(&msg); DispatchMessageW(&msg); }
    UnmapViewOfFile(r); CloseHandle(map); return 0;
}
static int load(const wchar_t *name) {
    HMODULE dll=LoadLibraryW(name); if(!dll) return 0;
    arm=(ARM)GetProcAddress(dll,"JSB_Arm"); cancel=(STATUS)GetProcAddress(dll,"JSB_Cancel");
    readstatus=(STATUS)GetProcAddress(dll,"JSB_ReadStatus"); fire=(FIRE)GetProcAddress(dll,"JSB_Fire");
    closebridge=(CLOSE)GetProcAddress(dll,"JSB_Close");
    /* Never FreeLibrary while hook or callback outcome could still be pending. */
    return arm&&cancel&&readstatus&&fire&&closebridge;
}
static int selftest(void) {
    wchar_t name[96],eventname[120],exe[MAX_PATH],command[MAX_PATH+160];
    HANDLE map,event; READY *r; STARTUPINFOW si; PROCESS_INFORMATION pi;
    JSB_STATUS s; DWORD_PTR ignored; DWORD started=GetTickCount(); int ok=0; DWORD cookie;
    if(!load(L"bridge_selftest.dll")) return 30;
    swprintf(name,96,L"Local\\JSBridgeSynthetic-%lu",(unsigned long)GetCurrentProcessId());
    swprintf(eventname,120,L"%ls-ready",name);
    map=CreateFileMappingW(INVALID_HANDLE_VALUE,NULL,PAGE_READWRITE,0,sizeof(READY),name);
    if(!map) return 31; r=(READY*)MapViewOfFile(map,FILE_MAP_ALL_ACCESS,0,0,sizeof(*r)); if(!r) return 32;
    event=CreateEventW(NULL,TRUE,FALSE,eventname); if(!event) return 33;
    GetModuleFileNameW(NULL,exe,MAX_PATH); swprintf(command,MAX_PATH+160,L"\"%ls\" --synthetic-child \"%ls\"",exe,name);
    ZeroMemory(&si,sizeof(si)); ZeroMemory(&pi,sizeof(pi)); si.cb=sizeof(si);
    if(!CreateProcessW(exe,command,NULL,NULL,FALSE,CREATE_NO_WINDOW,NULL,NULL,&si,&pi)) return 34;
    if(WaitForSingleObject(event,5000)!=WAIT_OBJECT_0) goto done;
    if(r->pid!=pi.dwProcessId||r->tid==GetCurrentThreadId()||!r->hwnd) goto done;
    /* Reject wrong HWND identity without installing a hook. */
    if(arm((HWND)(ULONG_PTR)r->hwnd,r->pid,r->tid+1,L"11111111111111111111111111111111")) goto done;
    if(arm((HWND)(ULONG_PTR)r->hwnd,r->pid,r->tid,L"bad-nonce")) goto done;
    if(!arm((HWND)(ULONG_PTR)r->hwnd,r->pid,r->tid,L"11111111111111111111111111111111")) goto done;
    readstatus(&s); cookie=s.cookie;
    /* Wrong nonce cookie must do nothing while the hook is active. */
    SendMessageTimeoutW((HWND)(ULONG_PTR)r->hwnd,WM_NULL,cookie^1,JSB_MESSAGE_TAG,SMTO_ABORTIFHUNG,2000,&ignored);
    readstatus(&s); if(s.state!=JSB_ARMED||s.calls) goto done;
    /* Correct message invokes once; repeat while hook remains installed tests CAS. */
    SendMessageTimeoutW((HWND)(ULONG_PTR)r->hwnd,WM_NULL,cookie,JSB_MESSAGE_TAG,SMTO_ABORTIFHUNG,2000,&ignored);
    SendMessageTimeoutW((HWND)(ULONG_PTR)r->hwnd,WM_NULL,cookie,JSB_MESSAGE_TAG,SMTO_ABORTIFHUNG,2000,&ignored);
    readstatus(&s); print_status("synthetic_one_shot",&s);
    if(s.state!=JSB_COMPLETE||s.calls!=1||s.callback_tid!=r->tid||!s.callback_finished||!s.retained_until_restart||s.mono_main_match) goto done;
    if(!cancel(&s)||!s.hook_removed||!closebridge()) goto done;
    /* A fresh arm then cancel must never execute the callback, even after a send. */
    if(!arm((HWND)(ULONG_PTR)r->hwnd,r->pid,r->tid,L"22222222222222222222222222222222")) goto done;
    readstatus(&s); cookie=s.cookie; if(!cancel(&s)) goto done;
    SendMessageTimeoutW((HWND)(ULONG_PTR)r->hwnd,WM_NULL,cookie,JSB_MESSAGE_TAG,SMTO_ABORTIFHUNG,2000,&ignored);
    readstatus(&s); print_status("synthetic_cancelled",&s);
    if(s.state!=JSB_CANCELLED||s.calls||!s.hook_removed||!closebridge()) goto done;
    /* Exercise production Fire wrapper's send + unhook sequence separately. */
    if(!arm((HWND)(ULONG_PTR)r->hwnd,r->pid,r->tid,L"33333333333333333333333333333333")) goto done;
    if(!fire(2000,&s)||s.calls!=1||!s.callback_finished||!s.hook_removed||!closebridge()) goto done;
    print_status("synthetic_fire",&s);
    /* Timeout removes the hook but does not cancel work already executing. */
    if(!arm((HWND)(ULONG_PTR)r->hwnd,r->pid,r->tid,L"44444444444444444444444444444444")) goto done;
    if(fire(10,&s)||s.state!=JSB_RUNNING||s.callback_finished||!s.hook_removed||closebridge()) goto done;
    print_status("synthetic_timeout_pending",&s);
    { DWORD until=GetTickCount()+3000;
      do { Sleep(10); readstatus(&s); } while(!s.callback_finished&&(LONG)(until-GetTickCount())>0);
    }
    print_status("synthetic_timeout_finished",&s);
    if(s.state!=JSB_COMPLETE||s.calls!=1||!s.callback_finished||!s.retained_until_restart||!closebridge()) goto done;
    ok=1;
done:
    /* This HWND belongs solely to the child created above. Never enumerate/game-attach. */
    if(r->hwnd) PostMessageW((HWND)(ULONG_PTR)r->hwnd,WM_CLOSE,0,0);
    if(WaitForSingleObject(pi.hProcess,5000)!=WAIT_OBJECT_0) {
        /* No forced termination; report incomplete synthetic cleanup. */
        printf("{\"event\":\"synthetic_cleanup_pending\",\"pid\":%lu}\n",(unsigned long)pi.dwProcessId); ok=0;
    }
    CloseHandle(pi.hThread); CloseHandle(pi.hProcess); CloseHandle(event); UnmapViewOfFile(r); CloseHandle(map);
    printf("{\"event\":\"synthetic_summary\",\"pass\":%s,\"elapsed_ms\":%lu}\n",ok?"true":"false",(unsigned long)(GetTickCount()-started));
    return ok?0:35;
}
int wmain(int argc,wchar_t **argv) {
    JSB_STATUS s; DWORD hwnd,pid,tid; int success; HANDLE target_lifetime;
    if(argc==3&&!wcscmp(argv[1],L"--synthetic-child")) return child(argv[2]);
    if(argc==2&&!wcscmp(argv[1],L"--self-test")) return selftest();
    if(argc!=6||wcscmp(argv[1],L"--run")) {
        fwprintf(stderr,L"Usage: bridge_host.exe --run <HWND hex> <PID decimal> <TID decimal> <nonce32>\nOr: bridge_selftest.exe --self-test\n"); return 2;
    }
    hwnd=wcstoul(argv[2],NULL,16); pid=wcstoul(argv[3],NULL,10); tid=wcstoul(argv[4],NULL,10);
    if(!load(L"bridge.dll")) { fprintf(stderr,"bridge_load_failed:%lu\n",(unsigned long)GetLastError()); return 3; }
    target_lifetime=OpenProcess(SYNCHRONIZE,FALSE,pid);
    if(!target_lifetime) { fprintf(stderr,"target_liveness_handle_failed:%lu\n",(unsigned long)GetLastError()); return 4; }
    if(!arm((HWND)(ULONG_PTR)hwnd,pid,tid,argv[5])) { fprintf(stderr,"arm_failed:%lu\n",(unsigned long)GetLastError()); CloseHandle(target_lifetime); return 4; }
    success=fire(5000,&s); print_status("native_fire_returned",&s);
    while(!s.hook_removed||s.state==JSB_RUNNING||(s.calls&&!s.callback_finished)) {
        /* Outcome unknown: retain controller/mapping; no retry/invocation/unload. */
        if(WaitForSingleObject(target_lifetime,0)==WAIT_OBJECT_0) {
            print_status("native_target_exited_cleanup_boundary",&s);
            /* The target process is proven gone. OS releases controller-only resources on exit. */
            CloseHandle(target_lifetime); return 7;
        }
        Sleep(100); if(!s.hook_removed) cancel(&s); else readstatus(&s);
    }
    print_status("native_final",&s);
    CloseHandle(target_lifetime); if(!closebridge()) return 6;
    return success&&s.state==JSB_COMPLETE?0:5;
}

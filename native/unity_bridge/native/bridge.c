#define UNICODE
#define _UNICODE
#define JSB_BUILD
#include "bridge.h"
#include <wincrypt.h>
#include <stdio.h>
#include <string.h>
#include <wchar.h>
#ifndef JSB_SYNTHETIC
#include "payload_seal.h"
#endif
#if !defined(_M_IX86) && !defined(__i386__)
#error This bridge must be built for x86.
#endif
#define JSB_SESSION_ROOT L"G:\\development\\enjoy\\jiangshen-assistant\\data\\object_sessions\\"
#define JSB_EXE_SHA "5cc05ce27aa4d2fc0474603b4867fb00e3247fa437e459ef36a7075eab4bbc70"
#define JSB_MONO_SHA "51941db23fd29380039924d1a44964fec18ab2a80e72f14410fe0163d2cae500"
typedef struct SHARED { JSB_STATUS s; wchar_t nonce[33]; wchar_t session[MAX_PATH]; } SHARED;
static HMODULE self;
static HANDLE mapping;
static SHARED *control;
static HHOOK hook;
/* No callbacks, threads, Mono calls, file I/O or hook operations in DllMain. */
BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID reserved) {
    (void)reserved; if(reason==DLL_PROCESS_ATTACH) self=h; return TRUE;
}
static void map_name(wchar_t *b,DWORD pid,DWORD tid) {
    swprintf(b,80,L"Local\\JSAssistantBridge1-%08lx-%08lx",(unsigned long)pid,(unsigned long)tid);
}
static int nonce_valid(const wchar_t *n) {
    int i; if(!n) return 0;
    for(i=0;i<32;i++) if(!((n[i]>=L'0'&&n[i]<=L'9')||(n[i]>=L'a'&&n[i]<=L'f'))) return 0;
    return n[32]==0;
}
static void detail(SHARED *c,DWORD code,const char *text) {
    c->s.result_code=code; lstrcpynA(c->s.detail,text,sizeof(c->s.detail));
}
static ULONGLONG process_creation(HANDLE process) {
    FILETIME created,exited,kernel,user; ULARGE_INTEGER value;
    if(!GetProcessTimes(process,&created,&exited,&kernel,&user)) return 0;
    value.LowPart=created.dwLowDateTime; value.HighPart=created.dwHighDateTime; return value.QuadPart;
}
#ifndef JSB_SYNTHETIC
static HANDLE sealed_payload=INVALID_HANDLE_VALUE;
static HANDLE sealed_cached_payload=INVALID_HANDLE_VALUE;
static int own_payload_path(const wchar_t *path) {
    const size_t prefix=sizeof(JSB_SESSION_ROOT)/sizeof(wchar_t)-1;
    wchar_t nonce[33];
    if(_wcsnicmp(path,JSB_SESSION_ROOT,prefix)||wcslen(path)!=prefix+32+wcslen(L"\\bin\\JSAssistantBridge.dll")) return 0;
    wcsncpy(nonce,path+prefix,32); nonce[32]=0;
    return nonce_valid(nonce)&&!_wcsicmp(path+prefix+32,L"\\bin\\JSAssistantBridge.dll");
}
static int module_path(HMODULE module,wchar_t *path) {
    DWORD size=GetModuleFileNameW(module,path,MAX_PATH); return size>0&&size<MAX_PATH;
}
static int hash_matches(const wchar_t *path,const char *expected,HANDLE *keep) {
    HANDLE f; HCRYPTPROV p=0; HCRYPTHASH h=0;
    BYTE buffer[8192],digest[32]; DWORD got=0,n=32; int ok=0,i; char hex[65]; LARGE_INTEGER size;
    /* Deny concurrent writers/deletion while reading the bounded PE file. */
    f=CreateFileW(path,GENERIC_READ,FILE_SHARE_READ,NULL,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,NULL);
    if(f==INVALID_HANDLE_VALUE) return 0;
    if(GetFileType(f)!=FILE_TYPE_DISK||!GetFileSizeEx(f,&size)||size.QuadPart<=0||size.QuadPart>32*1024*1024LL) goto done;
    if(!CryptAcquireContextW(&p,NULL,NULL,PROV_RSA_AES,CRYPT_VERIFYCONTEXT)) goto done;
    if(!CryptCreateHash(p,CALG_SHA_256,0,0,&h)) goto done;
    do { if(!ReadFile(f,buffer,sizeof(buffer),&got,NULL)) goto done;
        if(got&&!CryptHashData(h,buffer,got,0)) goto done;
    } while(got);
    if(!CryptGetHashParam(h,HP_HASHVAL,digest,&n,0)||n!=32) goto done;
    for(i=0;i<32;i++) sprintf(hex+i*2,"%02x",digest[i]); hex[64]=0;
    ok=!strcmp(hex,expected);
done: if(h) CryptDestroyHash(h); if(p) CryptReleaseContext(p,0);
    if(ok&&keep) *keep=f; else CloseHandle(f); return ok;
}
typedef void MonoDomain; typedef void MonoThread; typedef void MonoAssembly;
typedef void MonoImage; typedef void MonoClass; typedef void MonoMethod;
typedef void MonoObject; typedef void MonoString; typedef void MonoSignature; typedef void MonoType;
typedef void MonoTableInfo;
#define DECL(ret,name,args) typedef ret (__cdecl *name##_fn) args; name##_fn name
#define RESOLVE(name) name=(name##_fn)GetProcAddress(mono,#name); if(!name) { detail(c,12,"missing_mono_export:" #name); return 0; }
static int bootstrap(SHARED *c) {
    HMODULE mono=GetModuleHandleW(L"mono.dll"); wchar_t path[MAX_PATH],payload[MAX_PATH];
    char session_utf8[MAX_PATH*3],nonce[33]; int native_tid=(int)GetCurrentThreadId(),i;
    MonoDomain *domain,*root; MonoThread *current,*mainthread; MonoAssembly *assembly;
    MonoImage *image; MonoClass *klass; MonoMethod *method; MonoSignature *sig;
    MonoObject *exception=NULL,*result; MonoString *dirstr,*noncestr; void *args[3],*iter=NULL;
    const char *loaded_name;
    const MonoTableInfo *assembly_table; unsigned int assembly_row[9]; const char *assembly_identity;
    const int types[3]={0x0e,0x0e,0x08}; char *returned; unsigned int flags;
    DECL(MonoDomain*,mono_get_root_domain,(void)); DECL(MonoDomain*,mono_domain_get,(void));
    DECL(MonoThread*,mono_thread_current,(void)); DECL(MonoThread*,mono_thread_get_main,(void));
    DECL(MonoAssembly*,mono_domain_assembly_open,(MonoDomain*,const char*));
    DECL(MonoImage*,mono_assembly_get_image,(MonoAssembly*));
    DECL(const char*,mono_image_get_filename,(MonoImage*));
    DECL(int,mono_image_get_table_rows,(MonoImage*,int));
    DECL(const MonoTableInfo*,mono_image_get_table_info,(MonoImage*,int));
    DECL(void,mono_metadata_decode_row,(const MonoTableInfo*,int,unsigned int*,int));
    DECL(const char*,mono_metadata_string_heap,(MonoImage*,unsigned int));
    DECL(MonoClass*,mono_class_from_name,(MonoImage*,const char*,const char*));
    DECL(MonoMethod*,mono_class_get_method_from_name,(MonoClass*,const char*,int));
    DECL(MonoSignature*,mono_method_signature,(MonoMethod*));
    DECL(unsigned int,mono_method_get_flags,(MonoMethod*,unsigned int*));
    DECL(unsigned int,mono_signature_get_param_count,(MonoSignature*));
    DECL(MonoType*,mono_signature_get_return_type,(MonoSignature*));
    DECL(MonoType*,mono_signature_get_params,(MonoSignature*,void**));
    DECL(int,mono_type_get_type,(MonoType*)); DECL(int,mono_type_is_byref,(MonoType*));
    DECL(MonoString*,mono_string_new,(MonoDomain*,const char*));
    DECL(MonoObject*,mono_runtime_invoke,(MonoMethod*,void*,void**,MonoObject**));
    DECL(char*,mono_string_to_utf8,(MonoString*)); DECL(void,g_free,(void*));
    if(!mono) { detail(c,10,"mono_not_loaded"); return 0; }
    if(!module_path(NULL,path)||!hash_matches(path,JSB_EXE_SHA,NULL)) { detail(c,11,"client_identity_rejected"); return 0; }
    if(!module_path(mono,path)||!hash_matches(path,JSB_MONO_SHA,NULL)) { detail(c,11,"mono_identity_rejected"); return 0; }
    RESOLVE(mono_get_root_domain); RESOLVE(mono_domain_get); RESOLVE(mono_thread_current); RESOLVE(mono_thread_get_main);
    current=mono_thread_current(); mainthread=mono_thread_get_main();
    if(!current||!mainthread||current!=mainthread) { detail(c,13,"not_existing_mono_main_thread"); return 0; }
    c->s.mono_main_match=1; root=mono_get_root_domain(); domain=mono_domain_get();
    if(!root||!domain) { detail(c,14,"domain_missing"); return 0; }
    RESOLVE(mono_domain_assembly_open); RESOLVE(mono_assembly_get_image); RESOLVE(mono_image_get_filename); RESOLVE(mono_class_from_name);
    RESOLVE(mono_image_get_table_rows); RESOLVE(mono_image_get_table_info); RESOLVE(mono_metadata_decode_row); RESOLVE(mono_metadata_string_heap);
    RESOLVE(mono_class_get_method_from_name); RESOLVE(mono_method_signature); RESOLVE(mono_method_get_flags);
    RESOLVE(mono_signature_get_param_count); RESOLVE(mono_signature_get_return_type); RESOLVE(mono_signature_get_params);
    RESOLVE(mono_type_get_type); RESOLVE(mono_type_is_byref); RESOLVE(mono_string_new); RESOLVE(mono_runtime_invoke);
    RESOLVE(mono_string_to_utf8); RESOLVE(g_free);
    if(!module_path(self,payload)||!wcsrchr(payload,L'\\')) { detail(c,15,"bridge_path_missing"); return 0; }
    *wcsrchr(payload,L'\\')=0;
    if(wcslen(payload)+sizeof(L"\\JSAssistantBridge.dll")/sizeof(wchar_t)>=MAX_PATH) { detail(c,15,"bridge_path_long"); return 0; }
    wcscat(payload,L"\\JSAssistantBridge.dll");
    if(!own_payload_path(payload)) { detail(c,15,"bridge_path_outside_cache"); return 0; }
    /* A retained read-only, deny-write/delete handle closes the hash/load race. */
    if(sealed_payload==INVALID_HANDLE_VALUE&&!hash_matches(payload,JSB_PAYLOAD_SHA256,&sealed_payload)) { detail(c,16,"payload_seal_rejected"); return 0; }
    if(!WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,payload,-1,session_utf8,sizeof(session_utf8),NULL,NULL)) { detail(c,17,"payload_encoding"); return 0; }
    /* This is the only assembly load; no eval, type/method/path from the caller. */
    assembly=mono_domain_assembly_open(domain,session_utf8);
    if(!assembly||(image=mono_assembly_get_image(assembly))==NULL) { detail(c,18,"fixed_assembly_missing"); return 0; }
    loaded_name=mono_image_get_filename(image);
    if(!loaded_name||!MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,loaded_name,-1,path,MAX_PATH)||!own_payload_path(path)) { detail(c,18,"loaded_assembly_path_rejected"); return 0; }
    if(_wcsicmp(path,payload)&&!hash_matches(path,JSB_PAYLOAD_SHA256,sealed_cached_payload==INVALID_HANDLE_VALUE?&sealed_cached_payload:NULL)) { detail(c,18,"cached_assembly_seal_rejected"); return 0; }
    /* ECMA-335 Assembly table: 9 columns; column 7 is Name in #Strings. */
    if(mono_image_get_table_rows(image,0x20)!=1||(assembly_table=mono_image_get_table_info(image,0x20))==NULL) { detail(c,18,"assembly_table_rejected"); return 0; }
    mono_metadata_decode_row(assembly_table,0,assembly_row,9);
    assembly_identity=mono_metadata_string_heap(image,assembly_row[7]);
    if(!assembly_identity||strncmp(assembly_identity,JSB_ASSEMBLY_NAME,sizeof(JSB_ASSEMBLY_NAME))) { detail(c,18,"loaded_assembly_identity_rejected"); return 0; }
    if((klass=mono_class_from_name(image,"JiangShen.AssistantRuntime","Entry"))==NULL||
        (method=mono_class_get_method_from_name(klass,"Bootstrap",3))==NULL) { detail(c,18,"fixed_entry_missing"); return 0; }
    flags=mono_method_get_flags(method,NULL); sig=mono_method_signature(method);
    if(!(flags&0x10)||!sig||mono_signature_get_param_count(sig)!=3||mono_type_get_type(mono_signature_get_return_type(sig))!=0x0e) { detail(c,19,"entry_signature_rejected"); return 0; }
    for(i=0;i<3;i++) { MonoType *t=mono_signature_get_params(sig,&iter);
        if(!t||mono_type_is_byref(t)||mono_type_get_type(t)!=types[i]) { detail(c,19,"entry_parameter_rejected"); return 0; }
    }
    if(!WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,c->session,-1,session_utf8,sizeof(session_utf8),NULL,NULL)) { detail(c,17,"session_encoding"); return 0; }
    for(i=0;i<33;i++) nonce[i]=(char)c->nonce[i];
    dirstr=mono_string_new(domain,session_utf8); noncestr=mono_string_new(domain,nonce);
    if(!dirstr||!noncestr) { detail(c,20,"argument_allocation_failed"); return 0; }
    args[0]=dirstr; args[1]=noncestr; args[2]=&native_tid;
    result=mono_runtime_invoke(method,NULL,args,&exception);
    if(exception) { detail(c,21,"managed_exception_no_details"); return 0; }
    if(!result) { detail(c,22,"null_bootstrap_result"); return 0; }
    returned=mono_string_to_utf8((MonoString*)result);
    if(!returned) { detail(c,22,"result_encoding_failed"); return 0; }
    for(i=0;i<256&&returned[i];i++) if((unsigned char)returned[i]>127) break;
    if(i>=256||returned[i]) { g_free(returned); detail(c,23,"result_contract_rejected"); return 0; }
    detail(c,0,returned); g_free(returned);
    if(strncmp(c->s.detail,"BOOTSTRAP_OK;protocol=1;",sizeof("BOOTSTRAP_OK;protocol=1;")-1)!=0) { c->s.result_code=24; return 0; }
    return 1;
}
#endif
JSB_API LRESULT CALLBACK JSB_HookProc(int code,WPARAM wp,LPARAM lp) {
    CWPSTRUCT *m; wchar_t name[80]; HANDLE file; SHARED *c; DWORD owner=0; HMODULE retained;
    int executed=0; LRESULT next;
    if(code<0) return CallNextHookEx(NULL,code,wp,lp);
    m=(CWPSTRUCT*)lp;
    if(!m||m->message!=WM_NULL||(DWORD)m->lParam!=JSB_MESSAGE_TAG) return CallNextHookEx(NULL,code,wp,lp);
    map_name(name,GetCurrentProcessId(),GetCurrentThreadId());
    file=OpenFileMappingW(FILE_MAP_ALL_ACCESS,FALSE,name);
    if(!file) return CallNextHookEx(NULL,code,wp,lp);
    c=(SHARED*)MapViewOfFile(file,FILE_MAP_ALL_ACCESS,0,0,sizeof(SHARED));
    if(c) {
        if(c->s.protocol==JSB_PROTOCOL&&c->s.pid==GetCurrentProcessId()&&c->s.tid==GetCurrentThreadId()&&
            c->s.hwnd32==(DWORD)(ULONG_PTR)m->hwnd&&c->s.cookie==(DWORD)m->wParam&&
            GetWindowThreadProcessId(m->hwnd,&owner)==c->s.tid&&owner==c->s.pid&&nonce_valid(c->nonce)&&
            InterlockedCompareExchange(&c->s.state,JSB_RUNNING,JSB_ARMED)==JSB_ARMED) {
            int ok=0; executed=1; c->s.callback_tid=GetCurrentThreadId(); InterlockedIncrement(&c->s.calls);
            /* Keep native code until process exit, including timeout/unknown outcomes. */
            if(!c->s.expected_creation_filetime||process_creation(GetCurrentProcess())!=c->s.expected_creation_filetime) {
                detail(c,27,"process_creation_rejected");
            } else if(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,(LPCWSTR)(ULONG_PTR)&JSB_HookProc,&retained)) {
                c->s.retained_until_restart=1;
#ifdef JSB_SYNTHETIC
                if(c->nonce[0]==L'4') Sleep(250); /* Independent timeout/lifetime fixture only. */
                detail(c,0,"synthetic_callback_only_no_mono"); ok=1;
#else
                ok=bootstrap(c);
#endif
            } else { c->s.win32_error=GetLastError(); detail(c,25,"self_retention_failed"); }
            InterlockedExchange(&c->s.state,ok?JSB_COMPLETE:JSB_FAILED);
        }
    }
    next=CallNextHookEx(NULL,code,wp,lp);
    if(c) { if(executed) InterlockedExchange(&c->s.callback_finished,1); UnmapViewOfFile(c); }
    CloseHandle(file); return next;
}
JSB_API int __cdecl JSB_Arm(HWND hwnd,DWORD pid,DWORD tid,const wchar_t *nonce,ULONGLONG expected_creation_filetime) {
    wchar_t name[80]; DWORD owner=0,attributes; LARGE_INTEGER ticks; HANDLE process; ULONGLONG actual;
    if(control||!pid||!tid||!nonce_valid(nonce)||GetWindowThreadProcessId(hwnd,&owner)!=tid||owner!=pid) return 0;
    process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION|SYNCHRONIZE,FALSE,pid);
    if(!process) return 0; actual=process_creation(process); CloseHandle(process);
    if(!expected_creation_filetime||actual!=expected_creation_filetime) { SetLastError(ERROR_INVALID_PARAMETER); return 0; }
#ifdef JSB_SYNTHETIC
    { wchar_t classname[80];
      if(!GetClassNameW(hwnd,classname,80)||wcscmp(classname,L"JSAssistantSyntheticWindow")) return 0;
    }
#endif
    map_name(name,pid,tid);
    mapping=CreateFileMappingW(INVALID_HANDLE_VALUE,NULL,PAGE_READWRITE,0,sizeof(SHARED),name);
    if(!mapping) return 0;
    if(GetLastError()==ERROR_ALREADY_EXISTS) { CloseHandle(mapping); mapping=NULL; SetLastError(ERROR_ALREADY_EXISTS); return 0; }
    control=(SHARED*)MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,sizeof(SHARED));
    if(!control) { CloseHandle(mapping); mapping=NULL; return 0; }
    ZeroMemory(control,sizeof(*control)); control->s.protocol=JSB_PROTOCOL; control->s.pid=pid; control->s.tid=tid;
    control->s.expected_creation_filetime=expected_creation_filetime;
    control->s.hwnd32=(DWORD)(ULONG_PTR)hwnd; wcscpy(control->nonce,nonce);
    wcscpy(control->session,JSB_SESSION_ROOT); wcscat(control->session,nonce);
#ifndef JSB_SYNTHETIC
    attributes=GetFileAttributesW(control->session);
    if(attributes==INVALID_FILE_ATTRIBUTES||!(attributes&FILE_ATTRIBUTE_DIRECTORY)||(attributes&FILE_ATTRIBUTE_REPARSE_POINT)) { JSB_Close(); return 0; }
#else
    (void)attributes;
#endif
    QueryPerformanceCounter(&ticks); control->s.cookie=ticks.LowPart^GetCurrentProcessId()^GetTickCount();
    if(!control->s.cookie) control->s.cookie=1;
    InterlockedExchange(&control->s.state,JSB_ARMED);
    hook=SetWindowsHookExW(WH_CALLWNDPROC,JSB_HookProc,self,tid);
    if(!hook) { control->s.win32_error=GetLastError(); InterlockedExchange(&control->s.state,JSB_FAILED); JSB_Close(); return 0; }
    return 1;
}
JSB_API int __cdecl JSB_ReadStatus(JSB_STATUS *status) {
    if(!control||!status) return 0; MemoryBarrier(); CopyMemory(status,&control->s,sizeof(*status)); return 1;
}
static int unhook(void) {
    if(!hook) return 1;
    if(!UnhookWindowsHookEx(hook)) { control->s.win32_error=GetLastError(); return 0; }
    hook=NULL; control->s.hook_removed=1; return 1;
}
JSB_API int __cdecl JSB_Cancel(JSB_STATUS *status) {
    int removed; if(!control) return 0;
    InterlockedCompareExchange(&control->s.state,JSB_CANCELLED,JSB_ARMED);
    removed=unhook(); if(status) JSB_ReadStatus(status);
    return removed&&control->s.state!=JSB_RUNNING;
}
JSB_API int __cdecl JSB_Fire(DWORD timeout_ms,JSB_STATUS *status) {
    DWORD_PTR result=0; LRESULT sent; int removed;
    if(!control||!status||control->s.state!=JSB_ARMED||timeout_ms<1||timeout_ms>10000) return 0;
    SetLastError(0);
    sent=SendMessageTimeoutW((HWND)(ULONG_PTR)control->s.hwnd32,WM_NULL,control->s.cookie,JSB_MESSAGE_TAG,
        SMTO_ABORTIFHUNG|SMTO_BLOCK|SMTO_ERRORONEXIT,timeout_ms,&result);
    if(!sent) { control->s.win32_error=GetLastError(); InterlockedCompareExchange(&control->s.state,JSB_CANCELLED,JSB_ARMED); }
    else if(InterlockedCompareExchange(&control->s.state,JSB_FAILED,JSB_ARMED)==JSB_ARMED) {
        detail(control,26,"hook_callback_not_entered");
    }
    removed=unhook(); JSB_ReadStatus(status);
    return sent&&removed&&status->state==JSB_COMPLETE;
}
JSB_API int __cdecl JSB_Close(void) {
    if(!control) return 1;
    if(control->s.state==JSB_RUNNING||hook||(control->s.calls&&!control->s.callback_finished)) return 0;
    UnmapViewOfFile(control); CloseHandle(mapping); control=NULL; mapping=NULL; return 1;
}

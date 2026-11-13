#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <string.h>

/* Detached offline runtime host. No Unity/game assembly, process or transport. */
typedef void (__cdecl *set_dirs_fn)(const char *, const char *);
typedef void *(__cdecl *init_fn)(const char *, const char *);
typedef void *(__cdecl *open_fn)(void *, const char *);
typedef int (__cdecl *exec_fn)(void *, void *, int, char **);
typedef void (__cdecl *cleanup_fn)(void *);

int main(int argc, char **argv)
{
    if (argc < 5) { fprintf(stderr, "mono.dll managed-directory fixture.exe arguments required\n"); return 4; }
    char mono_directory[MAX_PATH], config_directory[MAX_PATH];
    if (strlen(argv[1]) >= MAX_PATH) return 4;
    strcpy(mono_directory, argv[1]);
    char *slash = strrchr(mono_directory, '\\');
    if (!slash) return 4;
    *slash = '\0';
    if (snprintf(config_directory, sizeof(config_directory), "%s\\etc", mono_directory) < 0) return 4;
    if (!SetDllDirectoryA(mono_directory)) return 4;
    HMODULE runtime = LoadLibraryA(argv[1]);
    if (!runtime) { fprintf(stderr, "mono_load_failed:%lu\n", GetLastError()); return 4; }
    set_dirs_fn set_dirs = (set_dirs_fn)GetProcAddress(runtime, "mono_set_dirs");
    init_fn init = (init_fn)GetProcAddress(runtime, "mono_jit_init_version");
    open_fn open = (open_fn)GetProcAddress(runtime, "mono_domain_assembly_open");
    exec_fn execute = (exec_fn)GetProcAddress(runtime, "mono_jit_exec");
    cleanup_fn cleanup = (cleanup_fn)GetProcAddress(runtime, "mono_jit_cleanup");
    if (!set_dirs || !init || !open || !execute || !cleanup) { fprintf(stderr, "mono_exports_missing\n"); return 4; }
    set_dirs(argv[2], config_directory);
    fprintf(stderr, "mono:set_dirs_ok\n"); fflush(stderr);
    void *domain = init("native-battle-store-offline", "v2.0.50727");
    fprintf(stderr, "mono:init_returned\n"); fflush(stderr);
    if (!domain) { fprintf(stderr, "mono_domain_failed\n"); return 4; }
    void *assembly = open(domain, argv[3]);
    fprintf(stderr, "mono:assembly_open_returned\n"); fflush(stderr);
    if (!assembly) { fprintf(stderr, "mono_fixture_load_failed\n"); return 4; }
    int result = execute(domain, assembly, argc - 3, argv + 3);
    fprintf(stderr, "mono:execute_returned:%d\n", result); fflush(stderr);
    cleanup(domain);
    return result;
}

#pragma once
#include <windows.h>
#ifdef __cplusplus
extern "C"
{
#endif
    extern HINSTANCE ScriptModule;
    unsigned ScriptDetectRuntimes(DWORD pid, LPCWSTR executable);
    void ScriptInitializePage(HWND window, DWORD pid, ULONGLONG creationTime, unsigned runtime);
    INT_PTR ScriptPageMessage(HWND window, UINT message, WPARAM wparam, LPARAM lparam);
    void ScriptShutdown(void);
    void ScriptSearchChanged(HWND window, ULONG_PTR match);
    HFONT ScriptCreateMonoFont(HWND window);
    BOOL ScriptIsDarkTheme(void);
    BOOL ScriptSearchMatches(ULONG_PTR match, LPCWSTR text);
    void ScriptInitializeSearch(HWND page, HWND edit);
    ULONG ScriptGetSetting(LPCWSTR suffix);
    void ScriptSetSetting(LPCWSTR suffix, ULONG value);
#ifdef __cplusplus
}
#endif

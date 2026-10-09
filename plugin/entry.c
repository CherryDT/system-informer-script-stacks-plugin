#include <phdk.h>
#include <settings.h>
#include <searchbox.h>
#include "bridge.h"
#include "resource.h"
#define PREFIX L"DavidTrapp.ScriptStacks."
HINSTANCE ScriptModule;
static PPH_PLUGIN Plugin;
static PH_CALLBACK_REGISTRATION PropertiesRegistration, UnloadRegistration;
ULONG ScriptGetSetting(LPCWSTR suffix)
{
    WCHAR name[160];
    _snwprintf_s(name, RTL_NUMBER_OF(name), _TRUNCATE, L"%s%s", PREFIX, suffix);
    return PhGetIntegerSetting(name);
}
void ScriptSetSetting(LPCWSTR suffix, ULONG value)
{
    WCHAR name[160];
    _snwprintf_s(name, RTL_NUMBER_OF(name), _TRUNCATE, L"%s%s", PREFIX, suffix);
    PhSetIntegerSetting(name, value);
}
BOOL ScriptIsDarkTheme(void)
{
    return !!PhGetIntegerSetting(L"EnableThemeSupport");
}
HFONT ScriptCreateMonoFont(HWND window)
{
    LOGFONTW font = {0};
    HFONT result = NULL;
    PPH_STRING setting = PhGetStringSetting(L"FontMonospace");
    // The convenience factory is not exported. Decode the host's saved font
    // using its public helpers, with a readable fixed-width fallback.
    if (setting->Length == sizeof(font) * 2 * sizeof(WCHAR) &&
        PhHexStringToBuffer(&setting->sr, (PUCHAR)&font))
    {
        font.lfFaceName[LF_FACESIZE - 1] = UNICODE_NULL;
        result = CreateFontIndirectW(&font);
    }
    PhDereferenceObject(setting);
    if (result)
        return result;
    return PhCreateFontHandle(L"Consolas", 9, FW_NORMAL, FIXED_PITCH, PhGetWindowDpi(window));
}
BOOL ScriptSearchMatches(ULONG_PTR match, LPCWSTR text)
{
    return !match || PhSearchControlMatchZ(match, text);
}
static VOID NTAPI SearchChanged(ULONG_PTR match, PVOID context)
{
    ScriptSearchChanged((HWND)context, match);
}
void ScriptInitializeSearch(HWND page, HWND edit)
{
    PhCreateSearchControl(page, edit, L"Search stacks (Ctrl+K)", SearchChanged, page);
}
static INT_PTR CALLBACK PageProc(HWND window, UINT message, WPARAM wparam, LPARAM lparam)
{
    PPH_PROCESS_PROPPAGECONTEXT page;
    PPH_PROCESS_ITEM process;
    if (!PhPropPageDlgProcHeader(window, message, lparam, NULL, &page, &process))
        return FALSE;
    if (message == WM_INITDIALOG)
    {
        ScriptInitializePage(window, HandleToUlong(process->ProcessId),
                             (ULONGLONG)process->CreateTime.QuadPart, (unsigned)(ULONG_PTR)page->Context);
        PhInitializeWindowTheme(window, !!PhGetIntegerSetting(L"EnableThemeSupport"));
        return TRUE;
    }
    if (message == WM_SHOWWINDOW && PhBeginPropPageLayout(window, page))
        PhEndPropPageLayout(window, page);
    return ScriptPageMessage(window, message, wparam, lparam);
}
static VOID NTAPI PropertiesInitializing(PVOID parameter, PVOID context)
{
    PPH_PLUGIN_PROCESS_PROPCONTEXT info = parameter;
    unsigned mask;
    UNREFERENCED_PARAMETER(context);
    if (!info->ProcessItem || !PH_IS_REAL_PROCESS_ID(info->ProcessItem->ProcessId))
        return;
    mask = ScriptGetSetting(L"AlwaysShowAllTabs")
               ? 7
               : ScriptDetectRuntimes(HandleToUlong(info->ProcessItem->ProcessId),
                                      PhGetString(info->ProcessItem->FileName));
    for (unsigned bit = 1; bit <= 4; bit <<= 1)
    {
        if (mask & bit)
        {
            PPH_PROCESS_PROPPAGECONTEXT page = PhCreateProcessPropPageContextEx(
                ScriptModule, MAKEINTRESOURCE(100 + bit), PageProc, (PVOID)(ULONG_PTR)bit);
            if (page && !PhAddProcessPropPage(info->PropContext, page))
                PhDereferenceObject(page);
        }
    }
}
static VOID NTAPI Unload(PVOID parameter, PVOID context)
{
    UNREFERENCED_PARAMETER(parameter);
    UNREFERENCED_PARAMETER(context);
    ScriptShutdown();
}
BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID reserved)
{
    UNREFERENCED_PARAMETER(reserved);
    if (reason == DLL_PROCESS_ATTACH)
    {
        PPH_PLUGIN_INFORMATION info;
        PH_SETTING_CREATE settings[] = {{IntegerSettingType, PREFIX L"AlwaysShowAllTabs", L"0"},
                                        {IntegerSettingType, PREFIX L"UseNodeInspectorWithoutAsking", L"0"},
                                        {IntegerSettingType, PREFIX L"CloseOwnedInspector", L"1"}};
        ScriptModule = module;
        Plugin = PhRegisterPlugin(L"DavidTrapp.ScriptStacks", module, &info);
        if (!Plugin)
            return FALSE;
        info->DisplayName = L"Script Stacks";
        info->Author = L"David Trapp / Trapp Innovations";
        info->Description = L"JavaScript, Python and Java thread stacks in process properties.";
        PhAddSettings(settings, RTL_NUMBER_OF(settings));
        PhRegisterCallback(PhGetGeneralCallback(GeneralCallbackProcessPropertiesInitializing),
                           PropertiesInitializing, NULL, &PropertiesRegistration);
        PhRegisterCallback(PhGetPluginCallback(Plugin, PluginCallbackUnload), Unload, NULL,
                           &UnloadRegistration);
    }
    return TRUE;
}

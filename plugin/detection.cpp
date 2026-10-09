#include "bridge.h"
#include <tlhelp32.h>
#include <algorithm>
#include <cwctype>
#include <string>
#include <vector>

namespace
{
std::wstring lower(std::wstring value)
{
    std::transform(value.begin(), value.end(), value.begin(), towlower);
    return value;
}
std::wstring filename(const std::wstring &path)
{
    return lower(path.substr(path.find_last_of(L"\\/") + 1));
}
unsigned moduleName(const std::wstring &name)
{
    if (name == L"node.exe" || name == L"node.dll" || name == L"electron.exe")
        return 1;
    if (name == L"jvm.dll" || name == L"java.exe" || name == L"javaw.exe")
        return 4;
    if (name == L"python.exe" || name == L"pythonw.exe")
        return 2;
    if (name.size() > 10 && name.compare(0, 6, L"python") == 0 && name.find(L".dll") != std::wstring::npos)
        return 2;
    return 0;
}
// OriginalFilename and product resources survive normal executable renaming.
// The loaded runtime libraries also identify embedded Python and JVM hosts.
unsigned versionEvidence(const std::wstring &path)
{
    DWORD ignored = 0;
    const auto size = GetFileVersionInfoSizeW(path.c_str(), &ignored);
    if (!size || size > 1024 * 1024)
        return 0;
    std::vector<BYTE> data(size);
    if (!GetFileVersionInfoW(path.c_str(), 0, size, data.data()))
        return 0;
    struct Translation
    {
        WORD language, codepage;
    };
    Translation *translations = nullptr;
    UINT length = 0;
    if (!VerQueryValueW(data.data(), L"\\VarFileInfo\\Translation", reinterpret_cast<void **>(&translations),
                        &length))
        return 0;
    unsigned mask = 0;
    for (UINT i = 0; i < length / sizeof(Translation) && i < 8; ++i)
    {
        for (auto field : {L"OriginalFilename", L"ProductName", L"FileDescription"})
        {
            wchar_t query[128];
            swprintf_s(query, L"\\StringFileInfo\\%04x%04x\\%s", translations[i].language,
                       translations[i].codepage, field);
            wchar_t *value = nullptr;
            UINT chars = 0;
            if (!VerQueryValueW(data.data(), query, reinterpret_cast<void **>(&value), &chars) || !chars)
                continue;
            const auto text = lower(std::wstring(value, wcsnlen_s(value, chars)));
            mask |= moduleName(text);
            if (text.find(L"node.js") != std::wstring::npos || text == L"electron")
                mask |= 1;
            if (text.find(L"python") != std::wstring::npos)
                mask |= 2;
            if (text.find(L"openjdk") != std::wstring::npos ||
                text.find(L"java virtual machine") != std::wstring::npos ||
                text.find(L"java hotspot") != std::wstring::npos)
                mask |= 4;
        }
    }
    return mask;
}
bool exists(const std::wstring &path)
{
    return GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES;
}
} // namespace
extern "C" unsigned ScriptDetectRuntimes(DWORD pid, LPCWSTR executable)
{
    try
    {
        unsigned mask = 0;
        wchar_t mappingName[80];
        swprintf_s(mappingName, L"node-debug-handler-%lu", pid);
        if (HANDLE mapping = OpenFileMappingW(FILE_MAP_READ, FALSE, mappingName))
        {
            mask |= 1;
            CloseHandle(mapping);
        }
        const std::wstring path = executable ? executable : L"";
        if (!path.empty())
            mask |= moduleName(filename(path)) | versionEvidence(path);
        HANDLE snapshot = INVALID_HANDLE_VALUE;
        for (int attempt = 0; attempt != 3; ++attempt)
        {
            snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
            if (snapshot != INVALID_HANDLE_VALUE || GetLastError() != ERROR_BAD_LENGTH)
                break;
        }
        bool chromium = false;
        if (snapshot != INVALID_HANDLE_VALUE)
        {
            MODULEENTRY32W module{sizeof(module)};
            wchar_t windows[MAX_PATH]{};
            GetWindowsDirectoryW(windows, MAX_PATH);
            const auto systemRoot = lower(std::wstring(windows)) + L"\\";
            unsigned count = 0;
            if (Module32FirstW(snapshot, &module))
                do
                {
                    const auto name = lower(module.szModule);
                    mask |= moduleName(name);
                    chromium |= name == L"chrome_elf.dll" || name == L"libegl.dll";
                    const auto modulePath = lower(module.szExePath);
                    // Avoid reading hundreds of Windows component version resources.
                    if (modulePath.compare(0, systemRoot.size(), systemRoot) != 0)
                        mask |= versionEvidence(module.szExePath);
                } while (++count < 512 && Module32NextW(snapshot, &module));
            CloseHandle(snapshot);
        }
        const auto folder = path.substr(0, path.find_last_of(L"\\/"));
        if (chromium &&
            (exists(folder + L"\\resources\\app.asar") || exists(folder + L"\\resources\\electron.asar") ||
             exists(folder + L"\\resources\\app\\package.json")))
            mask |= 1;
        return mask;
    }
    catch (...)
    {
        return 0;
    } // Detection must never break the host's properties window.
}

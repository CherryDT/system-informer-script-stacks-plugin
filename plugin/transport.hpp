#pragma once
#include <windows.h>
#include <atomic>
#include <string>
namespace script_stacks
{
struct Request
{
    std::string operation = "capture", runtime;
    DWORD pid = 0;
    ULONGLONG creationTime = 0;
    bool allowActivate = false, closeInspector = true;
};
struct Result
{
    std::string status = "error", toolId;
    std::wstring text, installLabel;
};
std::wstring FromUtf8(const std::string &value);
std::string ToUtf8(const std::wstring &value);
Result RunHelper(HINSTANCE module, const Request &request, const std::atomic_bool &cancelled);
} // namespace script_stacks
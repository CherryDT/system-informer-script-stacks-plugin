#include "transport.hpp"
#include "../vendor/json.hpp"
#include <objbase.h>
#include <vector>
#include <algorithm>
#include <stdexcept>

namespace script_stacks
{
namespace
{
class Handle
{
  public:
    HANDLE value = nullptr;
    Handle() = default;
    explicit Handle(HANDLE h) : value(h) {}
    ~Handle()
    {
        close();
    }
    Handle(const Handle &) = delete;
    Handle &operator=(const Handle &) = delete;
    void close()
    {
        if (value && value != INVALID_HANDLE_VALUE)
            CloseHandle(value);
        value = nullptr;
    }
};
std::wstring WinError(const wchar_t *action)
{
    DWORD code = GetLastError();
    wchar_t *message = nullptr;
    FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM |
                       FORMAT_MESSAGE_IGNORE_INSERTS,
                   nullptr, code, 0, reinterpret_cast<wchar_t *>(&message), 0, nullptr);
    std::wstring result = std::wstring(action) + L" (" + std::to_wstring(code) + L"): " +
                          (message ? message : L"Windows error");
    if (message)
        LocalFree(message);
    return result;
}
Result Error(std::wstring text)
{
    Result result;
    result.text = std::move(text);
    return result;
}
// Read a finite batch so noisy output cannot postpone cancellation.
bool Drain(HANDLE pipe, std::string &output, size_t limit, bool truncate)
{
    DWORD available = 0;
    if (!PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr))
        return GetLastError() == ERROR_BROKEN_PIPE;
    available = (std::min)(available, DWORD(65536));
    while (available)
    {
        char buffer[8192];
        DWORD read = 0;
        if (!ReadFile(pipe, buffer, (std::min)(available, DWORD(sizeof(buffer))), &read, nullptr))
            return GetLastError() == ERROR_BROKEN_PIPE;
        if (!read)
            break;
        size_t remaining = limit - output.size();
        if (!truncate && read > remaining)
            return false;
        output.append(buffer, (std::min)(remaining, size_t(read)));
        available -= read;
    }
    return true;
}
} // namespace
std::wstring FromUtf8(const std::string &value)
{
    if (value.empty())
        return {};
    int size =
        MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), int(value.size()), nullptr, 0);
    if (!size)
        throw std::runtime_error("Invalid UTF-8");
    std::wstring result(size, L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), int(value.size()), result.data(), size);
    return result;
}
std::string ToUtf8(const std::wstring &value)
{
    if (value.empty())
        return {};
    int size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), int(value.size()), nullptr, 0,
                                   nullptr, nullptr);
    if (!size)
        throw std::runtime_error("Invalid Unicode");
    std::string result(size, '\0');
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), int(value.size()), result.data(), size,
                        nullptr, nullptr);
    return result;
}
Result RunHelper(HINSTANCE module, const Request &request, const std::atomic_bool &cancelled)
{
    try
    {
        if (cancelled.load())
            return Error(L"Capture cancelled.");
        std::vector<wchar_t> moduleName(32768);
        DWORD length = GetModuleFileNameW(module, moduleName.data(), DWORD(moduleName.size()));
        if (!length || length >= moduleName.size())
            return Error(L"Cannot locate the plugin directory.");
        std::wstring executable(moduleName.data(), length);
        auto slash = executable.find_last_of(L"\\/");
        if (slash == std::wstring::npos)
            return Error(L"Cannot locate the plugin directory.");
        executable.resize(slash + 1);
        executable += L"ScriptStacks\\helpers\\x64\\ScriptStacks.Host.exe";
        if (GetFileAttributesW(executable.c_str()) == INVALID_FILE_ATTRIBUTES)
            return Error(L"The Script Stacks helper is missing. Reinstall the complete plugin package, "
                         L"including ScriptStacks\\helpers\\x64.");
        GUID cancellationGuid{};
        if (FAILED(CoCreateGuid(&cancellationGuid)))
            return Error(L"Cannot create helper cancellation identifier.");
        wchar_t guidText[40]{};
        if (!StringFromGUID2(cancellationGuid, guidText, ARRAYSIZE(guidText)))
            return Error(L"Cannot format helper cancellation identifier.");
        std::wstring cancellationName = L"Local\\ScriptStacks.Cancel." + std::wstring(guidText);
        Handle cancellationEvent(CreateEventW(nullptr, TRUE, FALSE, cancellationName.c_str()));
        if (!cancellationEvent.value)
            return Error(WinError(L"Cannot create helper cancellation event"));
        if (GetLastError() == ERROR_ALREADY_EXISTS)
            return Error(L"The helper cancellation identifier is already in use.");
        SECURITY_ATTRIBUTES security{sizeof(security), nullptr, TRUE};
        Handle inputRead, inputWrite, outputRead, outputWrite, errorRead, errorWrite;
        if (!CreatePipe(&inputRead.value, &inputWrite.value, &security, 0) ||
            !CreatePipe(&outputRead.value, &outputWrite.value, &security, 0) ||
            !CreatePipe(&errorRead.value, &errorWrite.value, &security, 0))
            return Error(WinError(L"Cannot create helper pipes"));
        if (!SetHandleInformation(inputWrite.value, HANDLE_FLAG_INHERIT, 0) ||
            !SetHandleInformation(outputRead.value, HANDLE_FLAG_INHERIT, 0) ||
            !SetHandleInformation(errorRead.value, HANDLE_FLAG_INHERIT, 0))
            return Error(WinError(L"Cannot protect helper pipes"));
        Handle job(CreateJobObjectW(nullptr, nullptr));
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!job.value ||
            !SetInformationJobObject(job.value, JobObjectExtendedLimitInformation, &limits, sizeof(limits)))
            return Error(WinError(L"Cannot create helper job"));
        SIZE_T attributeSize = 0;
        InitializeProcThreadAttributeList(nullptr, 1, 0, &attributeSize);
        std::vector<unsigned char> attributes(attributeSize);
        auto attributeList = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attributes.data());
        if (!InitializeProcThreadAttributeList(attributeList, 1, 0, &attributeSize))
            return Error(WinError(L"Cannot initialize helper handle list"));
        struct Cleanup
        {
            LPPROC_THREAD_ATTRIBUTE_LIST value;
            ~Cleanup()
            {
                DeleteProcThreadAttributeList(value);
            }
        } cleanup{attributeList};
        HANDLE inherited[] = {inputRead.value, outputWrite.value, errorWrite.value};
        if (!UpdateProcThreadAttribute(attributeList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, inherited,
                                       sizeof(inherited), nullptr, nullptr))
            return Error(WinError(L"Cannot restrict helper handles"));
        STARTUPINFOEXW startup{};
        startup.StartupInfo.cb = sizeof(startup);
        startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
        startup.StartupInfo.hStdInput = inputRead.value;
        startup.StartupInfo.hStdOutput = outputWrite.value;
        startup.StartupInfo.hStdError = errorWrite.value;
        startup.lpAttributeList = attributeList;
        PROCESS_INFORMATION process{};
        std::wstring command = L"\"" + executable + L"\"";
        if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, TRUE,
                            CREATE_NO_WINDOW | CREATE_SUSPENDED | EXTENDED_STARTUPINFO_PRESENT, nullptr,
                            nullptr, &startup.StartupInfo, &process))
            return Error(WinError(L"Cannot start Script Stacks helper"));
        Handle processHandle(process.hProcess), threadHandle(process.hThread);
        if (!AssignProcessToJobObject(job.value, processHandle.value))
        {
            auto text = WinError(L"Cannot isolate Script Stacks helper");
            TerminateProcess(processHandle.value, 1);
            return Error(std::move(text));
        }
        inputRead.close();
        outputWrite.close();
        errorWrite.close();
        if (ResumeThread(threadHandle.value) == DWORD(-1))
            return Error(WinError(L"Cannot resume Script Stacks helper"));
        threadHandle.close();
        nlohmann::json json{{"operation", request.operation},
                            {"runtime", request.runtime},
                            {"pid", request.pid},
                            {"creationTime", request.creationTime},
                            {"allowActivate", request.allowActivate},
                            {"closeInspector", request.closeInspector},
                            {"cancelEventName", ToUtf8(cancellationName)}};
        std::string serialized = json.dump() + "\n";
        DWORD written = 0;
        if (!WriteFile(inputWrite.value, serialized.data(), DWORD(serialized.size()), &written, nullptr) ||
            written != serialized.size())
            return Error(WinError(L"Cannot send helper request"));
        inputWrite.close();
        ULONGLONG start = GetTickCount64(), timeout = request.operation == "install" ? 330000 : 45000;
        std::string output, errors;
        ULONGLONG stopRequestedAt = 0;
        bool timedOut = false;
        for (;;)
        {
            const ULONGLONG now = GetTickCount64();
            if (!stopRequestedAt && (cancelled.load() || now - start >= timeout))
            {
                timedOut = !cancelled.load();
                stopRequestedAt = now;
                // Let the helper resume JavaScript and close an inspector it opened.
                SetEvent(cancellationEvent.value);
            }
            if (stopRequestedAt && now - stopRequestedAt >= 9000)
                return Error(timedOut ? L"The operation timed out. The target process was not terminated."
                                      : L"Capture cancelled.");
            if (!Drain(outputRead.value, output, 4 * 1024 * 1024, false))
                return Error(L"The helper output exceeded 4 MiB or its output pipe failed.");
            if (!Drain(errorRead.value, errors, 64 * 1024, true))
                return Error(L"Cannot read helper diagnostics.");
            if (WaitForSingleObject(processHandle.value, 20) == WAIT_OBJECT_0)
            {
                // Stop remaining owned child tools before draining the final finite buffers.
                job.close();
                for (;;)
                {
                    DWORD available = 0;
                    if (!PeekNamedPipe(outputRead.value, nullptr, 0, nullptr, &available, nullptr) ||
                        !available)
                        break;
                    if (!Drain(outputRead.value, output, 4 * 1024 * 1024, false))
                        return Error(L"The helper output exceeded 4 MiB.");
                }
                Drain(errorRead.value, errors, 64 * 1024, true);
                break;
            }
        }
        if (stopRequestedAt)
            return Error(timedOut ? L"The operation timed out. The target process was not terminated."
                                  : L"Capture cancelled.");
        if (output.empty())
            return Error(L"The helper returned no result." +
                         (errors.empty() ? std::wstring() : L"\r\n\r\n" + FromUtf8(errors)));
        auto response = nlohmann::json::parse(output);
        Result result;
        result.status = response.value("status", std::string("error"));
        result.text = FromUtf8(response.value("text", std::string()));
        if (response.contains("toolId") && !response["toolId"].is_null())
            result.toolId = response["toolId"].get<std::string>();
        if (response.contains("installLabel") && !response["installLabel"].is_null())
            result.installLabel = FromUtf8(response["installLabel"].get<std::string>());
        if (result.status != "ok" && result.status != "error" && result.status != "missing_tool" &&
            result.status != "needs_activation" && result.status != "unsupported")
            return Error(L"The helper returned an unknown result status.");
        return result;
    }
    catch (const std::exception &)
    {
        return Error(L"The helper returned an invalid response or could not allocate memory.");
    }
}
} // namespace script_stacks
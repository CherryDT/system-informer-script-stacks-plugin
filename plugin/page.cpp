#include "bridge.h"
#include "transport.hpp"
#include <commctrl.h>
#include <commdlg.h>
#include <algorithm>
#include <atomic>
#include <deque>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace
{
constexpr UINT ResultMessage = WM_APP + 217;
constexpr wchar_t StateProperty[] = L"ScriptStacks.NativePage.State";
enum
{
    CaptureId = 2001,
    ExportId,
    OptionsId,
    SearchId,
    ContentId,
    StatusId,
    CopySelectionId = 2101,
    CopyAllId,
    SaveId,
    ActivateOptionId,
    CloseOptionId,
    AllTabsOptionId
};
struct Completion
{
    script_stacks::Result result;
    bool installation;
};
struct Page
{
    HWND window = nullptr, capture = nullptr, exportButton = nullptr, options = nullptr, search = nullptr,
         content = nullptr, status = nullptr;
    HFONT mono = nullptr;
    DWORD pid = 0;
    ULONGLONG creationTime = 0;
    unsigned runtime = 0;
    ULONG_PTR cookie = 0, match = 0;
    std::wstring text;
    std::atomic_bool cancelled{false};
    std::mutex mailboxMutex;
    std::deque<Completion> mailbox;
    bool alive = true, busy = false;
};
struct Worker
{
    std::thread thread;
    std::shared_ptr<std::atomic_bool> done;
};
std::mutex workersMutex;
std::vector<Worker> workers;
std::vector<std::weak_ptr<Page>> pages;
std::atomic<ULONG_PTR> nextCookie{1};
using PagePointer = std::shared_ptr<Page>;
PagePointer GetPage(HWND window)
{
    auto pointer = reinterpret_cast<PagePointer *>(GetPropW(window, StateProperty));
    return pointer ? *pointer : PagePointer{};
}
const wchar_t *RuntimeName(unsigned runtime)
{
    return runtime == 1 ? L"JS" : runtime == 2 ? L"Python" : L"Java";
}
const char *RuntimeKey(unsigned runtime)
{
    return runtime == 1 ? "js" : runtime == 2 ? "python" : "java";
}
std::wstring CaptureLabel(const Page &page)
{
    return L"Capture " + std::wstring(RuntimeName(page.runtime)) + L" stacks";
}
int Scale(HWND window, int value)
{
    return MulDiv(value, int(GetDpiForWindow(window)), 96);
}
void Layout(Page &page)
{
    RECT client{};
    GetClientRect(page.window, &client);
    int margin = Scale(page.window, 6), gap = Scale(page.window, 5), height = Scale(page.window, 23);
    int button = Scale(page.window, 128), exportWidth = Scale(page.window, 96),
        optionsWidth = Scale(page.window, 78);
    int width = (std::max)(0L, client.right - 2 * margin);
    int searchWidth = width - button - exportWidth - optionsWidth - 3 * gap;
    bool wrap = searchWidth < Scale(page.window, 90);
    int y = margin, x = margin;
    MoveWindow(page.capture, x, y, button, height, TRUE);
    x += button + gap;
    MoveWindow(page.exportButton, x, y, exportWidth, height, TRUE);
    x += exportWidth + gap;
    MoveWindow(page.options, x, y, optionsWidth, height, TRUE);
    x += optionsWidth + gap;
    if (wrap)
    {
        y += height + gap;
        x = margin;
        searchWidth = width;
    }
    MoveWindow(page.search, x, y, (std::max)(0, searchWidth), height, TRUE);
    int contentTop = y + height + gap, statusHeight = Scale(page.window, 20);
    int contentHeight = (std::max)(0L, client.bottom - margin - statusHeight - gap - contentTop);
    MoveWindow(page.content, margin, contentTop, width, contentHeight, TRUE);
    MoveWindow(page.status, margin, contentTop + contentHeight + gap, width, statusHeight, TRUE);
}
std::wstring NormalizeLines(const std::wstring &text)
{
    std::wstring result;
    result.reserve(text.size());
    for (size_t i = 0; i < text.size(); ++i)
    {
        wchar_t ch = text[i];
        if (ch == L'\r')
        {
            result += L"\r\n";
            if (i + 1 < text.size() && text[i + 1] == L'\n')
                ++i;
        }
        else if (ch == L'\n')
            result += L"\r\n";
        else if (ch != L'\0')
            result += ch;
    }
    return result;
}
void Render(Page &page)
{
    if (!page.match)
    {
        SetWindowTextW(page.content, page.text.c_str());
        return;
    }
    std::vector<std::wstring> lines;
    size_t begin = 0;
    while (begin <= page.text.size())
    {
        size_t end = page.text.find(L'\n', begin);
        std::wstring line = page.text.substr(begin, end == std::wstring::npos ? end : end - begin);
        if (!line.empty() && line.back() == L'\r')
            line.pop_back();
        lines.push_back(std::move(line));
        if (end == std::wstring::npos)
            break;
        begin = end + 1;
    }
    std::vector<bool> include(lines.size(), false);
    size_t heading = 0;
    bool any = false;
    for (size_t i = 0; i < lines.size(); ++i)
    {
        const auto &line = lines[i];
        if (!line.empty() &&
            (line.rfind(L"Thread", 0) == 0 || line.rfind(L"thread", 0) == 0 || line == L"Main thread" ||
             line.rfind(L"Worker ", 0) == 0 || line.rfind(L"# Thread", 0) == 0 || line.rfind(L"\"", 0) == 0))
            heading = i;
        if (ScriptSearchMatches(page.match, line.c_str()))
        {
            include[i] = true;
            include[heading] = true;
            any = true;
        }
    }
    std::wstring filtered;
    for (size_t i = 0; i < lines.size(); ++i)
        if (include[i])
        {
            filtered += lines[i];
            filtered += L"\r\n";
        }
    if (!any)
        filtered = L"No matching stack frames.";
    SetWindowTextW(page.content, filtered.c_str());
}
void SetBusy(Page &page, bool busy, bool installation = false)
{
    page.busy = busy;
    SetWindowTextW(page.capture, busy ? L"Cancel" : CaptureLabel(page).c_str());
    EnableWindow(page.options, !busy);
    SetWindowTextW(page.status,
                   busy ? (installation ? L"Installing tool in the private cache..." : L"Capturing stacks...")
                        : L"Ready");
}
void Begin(const PagePointer &page, bool install = false, bool activate = false)
{
    if (page->busy)
    {
        page->cancelled.store(true);
        SetWindowTextW(page->status, L"Cancelling...");
        return;
    }
    page->cancelled.store(false);
    SetBusy(*page, true, install);
    page->text = install ? L"Installing capture tool..." : L"Capturing...";
    SetWindowTextW(page->content, page->text.c_str());
    script_stacks::Request request;
    request.operation = install ? "install" : "capture";
    request.runtime = RuntimeKey(page->runtime);
    request.pid = page->pid;
    request.creationTime = page->creationTime;
    request.allowActivate = activate || ScriptGetSetting(L"UseNodeInspectorWithoutAsking") != 0;
    request.closeInspector = ScriptGetSetting(L"CloseOwnedInspector") != 0;
    auto done = std::make_shared<std::atomic_bool>(false);
    std::lock_guard<std::mutex> guard(workersMutex);
    for (auto it = workers.begin(); it != workers.end();)
    {
        if (it->done->load())
        {
            if (it->thread.joinable())
                it->thread.join();
            it = workers.erase(it);
        }
        else
            ++it;
    }
    try
    {
        workers.reserve(workers.size() + 1);
        workers.push_back(Worker{std::thread([page, request, done, install] {
                                     auto result =
                                         script_stacks::RunHelper(ScriptModule, request, page->cancelled);
                                     {
                                         std::lock_guard<std::mutex> lock(page->mailboxMutex);
                                         if (page->alive)
                                         {
                                             page->mailbox.push_back({std::move(result), install});
                                             PostMessageW(page->window, ResultMessage, page->cookie, 0);
                                         }
                                     }
                                     done->store(true);
                                 }),
                                 done});
    }
    catch (...)
    {
        SetBusy(*page, false);
        page->text = L"Could not start capture worker.";
        SetWindowTextW(page->content, page->text.c_str());
        SetWindowTextW(page->status, page->text.c_str());
    }
}
std::wstring VisibleText(HWND edit)
{
    int length = GetWindowTextLengthW(edit);
    std::wstring text(size_t(length) + 1, L'\0');
    GetWindowTextW(edit, text.data(), length + 1);
    text.resize(length);
    return text;
}
std::wstring Selection(Page &page)
{
    DWORD first = 0, last = 0;
    SendMessageW(page.content, EM_GETSEL, reinterpret_cast<WPARAM>(&first), reinterpret_cast<LPARAM>(&last));
    auto text = VisibleText(page.content);
    if (first > text.size() || last <= first)
        return {};
    return text.substr(first, last - first);
}
void Copy(Page &page, bool all)
{
    auto text = all ? page.text : Selection(page);
    if (text.empty())
        return;
    HGLOBAL memory = GlobalAlloc(GMEM_MOVEABLE, (text.size() + 1) * sizeof(wchar_t));
    if (!memory)
        return;
    void *pointer = GlobalLock(memory);
    if (!pointer)
    {
        GlobalFree(memory);
        return;
    }
    memcpy(pointer, text.c_str(), (text.size() + 1) * sizeof(wchar_t));
    GlobalUnlock(memory);
    if (!OpenClipboard(page.window))
    {
        GlobalFree(memory);
        return;
    }
    EmptyClipboard();
    if (!SetClipboardData(CF_UNICODETEXT, memory))
        GlobalFree(memory);
    CloseClipboard();
}
void Save(Page &page)
{
    wchar_t filename[32768]{};
    std::wstring name =
        std::wstring(RuntimeName(page.runtime)) + L"-" + std::to_wstring(page.pid) + L"-stacks.txt";
    wcscpy_s(filename, name.c_str());
    OPENFILENAMEW dialog{sizeof(dialog)};
    dialog.hwndOwner = page.window;
    dialog.lpstrFile = filename;
    dialog.nMaxFile = ARRAYSIZE(filename);
    dialog.lpstrFilter = L"Text files (*.txt)\0*.txt\0All files (*.*)\0*.*\0";
    dialog.lpstrDefExt = L"txt";
    dialog.Flags = OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR;
    if (!GetSaveFileNameW(&dialog))
        return;
    try
    {
        auto utf8 = script_stacks::ToUtf8(page.text);
        HANDLE file =
            CreateFileW(filename, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            MessageBoxW(page.window, L"The stack text could not be saved.", L"Script Stacks",
                        MB_OK | MB_ICONERROR);
            return;
        }
        DWORD written = 0;
        BOOL ok = WriteFile(file, utf8.data(), DWORD(utf8.size()), &written, nullptr);
        CloseHandle(file);
        if (!ok || written != utf8.size())
            MessageBoxW(page.window, L"The complete stack text could not be saved.", L"Script Stacks",
                        MB_OK | MB_ICONERROR);
        else
            SetWindowTextW(page.status, L"Stack text saved.");
    }
    catch (...)
    {
        MessageBoxW(page.window, L"The stack text could not be encoded as UTF-8.", L"Script Stacks",
                    MB_OK | MB_ICONERROR);
    }
}
UINT Menu(HWND button, HMENU menu)
{
    RECT rect{};
    GetWindowRect(button, &rect);
    UINT command = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_LEFTALIGN | TPM_TOPALIGN,
                                    rect.left, rect.bottom, GetParent(button), nullptr);
    DestroyMenu(menu);
    return command;
}
void ExportMenu(Page &page)
{
    HMENU menu = CreatePopupMenu();
    AppendMenuW(menu, MF_STRING | (Selection(page).empty() ? MF_GRAYED : 0), CopySelectionId,
                L"Copy selection\tCtrl+C");
    AppendMenuW(menu, MF_STRING, CopyAllId, L"Copy all");
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING, SaveId, L"Save all as...");
    switch (Menu(page.exportButton, menu))
    {
    case CopySelectionId:
        Copy(page, false);
        break;
    case CopyAllId:
        Copy(page, true);
        break;
    case SaveId:
        Save(page);
        break;
    }
}
void OptionsMenu(Page &page)
{
    HMENU menu = CreatePopupMenu();
    if (page.runtime == 1)
    {
        AppendMenuW(menu, MF_STRING | (ScriptGetSetting(L"UseNodeInspectorWithoutAsking") ? MF_CHECKED : 0),
                    ActivateOptionId, L"Enable Node inspector without asking");
        AppendMenuW(menu, MF_STRING | (ScriptGetSetting(L"CloseOwnedInspector") ? MF_CHECKED : 0),
                    CloseOptionId, L"Close inspector opened by this plugin");
        AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    }
    AppendMenuW(menu, MF_STRING | (ScriptGetSetting(L"AlwaysShowAllTabs") ? MF_CHECKED : 0), AllTabsOptionId,
                L"Show all runtime tabs (reopen properties)");
    auto command = Menu(page.options, menu);
    const wchar_t *key = command == ActivateOptionId  ? L"UseNodeInspectorWithoutAsking"
                         : command == CloseOptionId   ? L"CloseOwnedInspector"
                         : command == AllTabsOptionId ? L"AlwaysShowAllTabs"
                                                      : nullptr;
    if (key)
        ScriptSetSetting(key, ScriptGetSetting(key) ? 0 : 1);
}
bool Prompt(HWND window, const std::wstring &title, const std::wstring &text, const wchar_t *action,
            bool *remember = nullptr)
{
    TASKDIALOG_BUTTON buttons[]{{100, action}, {IDCANCEL, L"Cancel"}};
    TASKDIALOGCONFIG dialog{sizeof(dialog)};
    dialog.hwndParent = window;
    dialog.dwFlags = TDF_ALLOW_DIALOG_CANCELLATION | TDF_SIZE_TO_CONTENT;
    dialog.pszWindowTitle = L"Script Stacks";
    dialog.pszMainInstruction = title.c_str();
    dialog.pszContent = text.c_str();
    dialog.cButtons = ARRAYSIZE(buttons);
    dialog.pButtons = buttons;
    dialog.nDefaultButton = IDCANCEL;
    dialog.pszMainIcon = TD_INFORMATION_ICON;
    if (remember)
        dialog.pszVerificationText = L"Don't ask again";
    int result = IDCANCEL;
    BOOL checked = FALSE;
    HRESULT hr = TaskDialogIndirect(&dialog, &result, nullptr, &checked);
    if (remember)
        *remember = checked != FALSE;
    return SUCCEEDED(hr) && result == 100;
}
void Complete(const PagePointer &page, Completion completion)
{
    SetBusy(*page, false);
    auto &result = completion.result;
    if (page->cancelled.load())
    {
        page->text = L"Capture cancelled.";
        SetWindowTextW(page->content, page->text.c_str());
        SetWindowTextW(page->status, L"Cancelled.");
        return;
    }
    if (result.status == "missing_tool" || result.status == "needs_activation")
    {
        page->text = NormalizeLines(result.text);
        Render(*page);
    }
    if (result.status == "missing_tool")
    {
        std::wstring explanation =
            result.text +
            L"\r\n\r\nDownload the required tool into Script Stacks' private cache, then capture stacks?";
        if (Prompt(page->window, L"A capture tool is required", explanation, L"Install and capture"))
            Begin(page, true);
        else
            SetWindowTextW(page->status, L"Tool installation cancelled.");
        return;
    }
    if (result.status == "needs_activation")
    {
        bool remember = false;
        if (Prompt(page->window, L"Temporarily enable the Node inspector?",
                   result.text + L"\r\n\r\nThe inspector allows debugging access to this process. Script "
                                 L"Stacks closes an inspector it opens when the corresponding option is "
                                 L"enabled; existing inspectors are preserved.",
                   L"Temporarily enable inspector", &remember))
        {
            if (remember)
                ScriptSetSetting(L"UseNodeInspectorWithoutAsking", 1);
            Begin(page, false, true);
        }
        else
            SetWindowTextW(page->status, L"Inspector activation cancelled.");
        return;
    }
    if (completion.installation && result.status == "ok")
    {
        Begin(page);
        return;
    }
    page->text =
        NormalizeLines(result.text.empty() ? (result.status == "ok" ? L"No stack frames were returned."
                                                                    : L"The stack capture failed.")
                                           : result.text);
    Render(*page);
    SetWindowTextW(page->status, result.status == "ok" ? L"Capture complete."
                                 : result.status == "unsupported"
                                     ? L"This runtime or target is not supported."
                                     : L"Capture failed.");
}
LRESULT CALLBACK EditSubclass(HWND window, UINT message, WPARAM wparam, LPARAM lparam, UINT_PTR,
                              DWORD_PTR parent)
{
    if (message == WM_KEYDOWN)
    {
        auto page = GetPage(reinterpret_cast<HWND>(parent));
        if (page)
        {
            if (wparam == VK_F5)
            {
                Begin(page);
                return 0;
            }
            if ((GetKeyState(VK_CONTROL) & 0x8000) && wparam == 'K')
            {
                SetFocus(page->search);
                SendMessageW(page->search, EM_SETSEL, 0, -1);
                return 0;
            }
        }
    }
    return DefSubclassProc(window, message, wparam, lparam);
}
HWND Control(HWND parent, const wchar_t *type, const wchar_t *text, DWORD style, int id)
{
    HWND control = CreateWindowExW(0, type, text, WS_CHILD | WS_VISIBLE | style, 0, 0, 0, 0, parent,
                                   reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)), ScriptModule, nullptr);
    SendMessageW(control, WM_SETFONT, SendMessageW(parent, WM_GETFONT, 0, 0), FALSE);
    return control;
}
} // namespace
extern "C" void ScriptInitializePage(HWND window, DWORD pid, ULONGLONG creationTime, unsigned runtime)
{
    auto page = std::make_shared<Page>();
    page->window = window;
    page->pid = pid;
    page->creationTime = creationTime;
    page->runtime = runtime;
    page->cookie = nextCookie.fetch_add(1);
    auto storage = new PagePointer(page);
    if (!SetPropW(window, StateProperty, storage))
    {
        delete storage;
        return;
    }
    {
        std::lock_guard<std::mutex> lock(workersMutex);
        pages.erase(std::remove_if(pages.begin(), pages.end(),
                                   [](const auto &candidate) { return candidate.expired(); }),
                    pages.end());
        pages.emplace_back(page);
    }
    page->capture =
        Control(window, WC_BUTTONW, CaptureLabel(*page).c_str(), WS_TABSTOP | BS_PUSHBUTTON, CaptureId);
    page->exportButton =
        Control(window, WC_BUTTONW, L"Copy / Save \x25BE", WS_TABSTOP | BS_PUSHBUTTON, ExportId);
    page->options = Control(window, WC_BUTTONW, L"Options \x25BE", WS_TABSTOP | BS_PUSHBUTTON, OptionsId);
    page->search = Control(window, WC_EDITW, L"", WS_TABSTOP | ES_AUTOHSCROLL, SearchId);
    page->content = Control(window, WC_EDITW, L"",
                            WS_TABSTOP | WS_BORDER | WS_VSCROLL | WS_HSCROLL | ES_MULTILINE | ES_AUTOVSCROLL |
                                ES_AUTOHSCROLL | ES_READONLY | ES_NOHIDESEL,
                            ContentId);
    page->status = Control(window, WC_STATICW, L"Ready", SS_LEFT | SS_ENDELLIPSIS, StatusId);
    SendMessageW(page->content, EM_SETLIMITTEXT, 8 * 1024 * 1024, 0);
    page->mono = ScriptCreateMonoFont(window);
    if (page->mono)
        SendMessageW(page->content, WM_SETFONT, reinterpret_cast<WPARAM>(page->mono), FALSE);
    SendMessageW(page->search, EM_SETCUEBANNER, TRUE, reinterpret_cast<LPARAM>(L"Search stacks (Ctrl+K)"));
    ScriptInitializeSearch(window, page->search);
    for (HWND control : {page->capture, page->exportButton, page->options, page->search, page->content})
        SetWindowSubclass(control, EditSubclass, 1, reinterpret_cast<DWORD_PTR>(window));
    std::wstring tool = runtime == 1 ? L"Node.js stacks use its inspector. Enabling an inspector requires "
                                       L"your confirmation. Electron renderer stacks are not supported."
                        : runtime == 2 ? L"Python stacks are captured with py-spy."
                                       : L"Java stacks are captured with jcmd from a matching JDK.";
    page->text = L"Press \"" + CaptureLabel(*page) + L"\" to take a stack snapshot.\r\n\r\n" + tool +
                 L" Missing tools can be installed in a private cache after confirmation.\r\n\r\nF5 captures "
                 L"stacks. Ctrl+K searches the captured text.";
    Render(*page);
    Layout(*page);
}
extern "C" INT_PTR ScriptPageMessage(HWND window, UINT message, WPARAM wparam, LPARAM)
{
    auto page = GetPage(window);
    if (!page)
        return FALSE;
    switch (message)
    {
    case WM_CTLCOLORDLG:
    case WM_CTLCOLORSTATIC:
    case WM_CTLCOLOREDIT:
        if (!ScriptIsDarkTheme())
        {
            HDC dc = reinterpret_cast<HDC>(wparam);
            SetTextColor(dc, GetSysColor(COLOR_WINDOWTEXT));
            SetBkColor(dc, GetSysColor(COLOR_WINDOW));
            return reinterpret_cast<INT_PTR>(GetSysColorBrush(COLOR_WINDOW));
        }
        break;
    case WM_SIZE:
        Layout(*page);
        return TRUE;
    case WM_DPICHANGED_AFTERPARENT: {
        HFONT replacement = ScriptCreateMonoFont(window);
        if (replacement)
        {
            SendMessageW(page->content, WM_SETFONT, reinterpret_cast<WPARAM>(replacement), TRUE);
            if (page->mono)
                DeleteObject(page->mono);
            page->mono = replacement;
        }
        HFONT normal = reinterpret_cast<HFONT>(SendMessageW(window, WM_GETFONT, 0, 0));
        for (HWND control : {page->capture, page->exportButton, page->options, page->search, page->status})
            SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(normal), TRUE);
        Layout(*page);
        return TRUE;
    }
    case WM_COMMAND:
        if (HIWORD(wparam) != BN_CLICKED)
            break;
        switch (LOWORD(wparam))
        {
        case CaptureId:
            Begin(page);
            return TRUE;
        case ExportId:
            ExportMenu(*page);
            return TRUE;
        case OptionsId:
            OptionsMenu(*page);
            return TRUE;
        }
        break;
    case WM_KEYDOWN:
        if (wparam == VK_F5)
        {
            Begin(page);
            return TRUE;
        }
        if ((GetKeyState(VK_CONTROL) & 0x8000) && wparam == 'K')
        {
            SetFocus(page->search);
            return TRUE;
        }
        break;
    case ResultMessage: {
        if (wparam != page->cookie)
            return TRUE;
        std::deque<Completion> mailbox;
        {
            std::lock_guard<std::mutex> lock(page->mailboxMutex);
            mailbox.swap(page->mailbox);
        }
        for (auto &completion : mailbox)
            Complete(page, std::move(completion));
        return TRUE;
    }
    case WM_DESTROY: {
        {
            std::lock_guard<std::mutex> lock(page->mailboxMutex);
            page->alive = false;
            page->window = nullptr;
            page->mailbox.clear();
        }
        page->cancelled.store(true);
        for (HWND control : {page->capture, page->exportButton, page->options, page->search, page->content})
            RemoveWindowSubclass(control, EditSubclass, 1);
        if (page->mono)
        {
            SendMessageW(page->content, WM_SETFONT, 0, FALSE);
            DeleteObject(page->mono);
            page->mono = nullptr;
        }
        delete reinterpret_cast<PagePointer *>(RemovePropW(window, StateProperty));
        return TRUE;
    }
    }
    return FALSE;
}
extern "C" void ScriptSearchChanged(HWND window, ULONG_PTR match)
{
    auto page = GetPage(window);
    if (page)
    {
        page->match = match;
        Render(*page);
    }
}
extern "C" void ScriptShutdown(void)
{
    std::vector<Worker> pending;
    {
        std::lock_guard<std::mutex> lock(workersMutex);
        for (auto &weak : pages)
            if (auto page = weak.lock())
            {
                page->cancelled.store(true);
                std::lock_guard<std::mutex> mailboxLock(page->mailboxMutex);
                page->alive = false;
                page->window = nullptr;
                page->mailbox.clear();
            }
        pending.swap(workers);
        pages.clear();
    }
    for (auto &worker : pending)
        if (worker.thread.joinable())
            worker.thread.join();
}
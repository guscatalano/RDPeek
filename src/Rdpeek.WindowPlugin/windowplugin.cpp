// rdpeek-window-plugin — a tiny NATIVE, IN-PROCESS DVC AddIn whose only job is to manipulate the
// mstsc window from inside mstsc.exe.
//
// It's the "control plane" companion to RDPeek's out-of-process diag plugin: registered as a second
// AddIn under InProcServer32, mstsc loads THIS dll into its own process, so it can touch the session
// window directly (move, resize, dock, topmost, a HUD overlay) — things an out-of-process plugin
// can't do cleanly. Kept deliberately tiny (no CLR, one file) so its crash surface is a fraction of
// the full plugin; the heavy logic stays in the crash-isolated C#/Companion side, which drives this
// over a simple text pipe (\\.\pipe\rdpeek-window).
//
// On Connected() it finds mstsc's top-level window (class TscShellContainerClass) for this process,
// tags its title as proof of life, shows a HUD overlay, and starts the control pipe. Commands
// (newline- or message-delimited UTF-8):
//   title <text>            append <text> to the window title (empty restores)
//   move <x> <y> <w> <h>    reposition/resize
//   topmost on|off          toggle always-on-top
//   show min|max|restore    window state
//   overlay <text>          show/update the HUD banner over the session (empty text hides it)
//   flash                   flash the taskbar button

#include <windows.h>
#include <unknwn.h>
#include <new>
#include <string>
#include <cstdio>
#include <cstdarg>

// ---- identity ------------------------------------------------------------------------------------
// Distinct from RDPeek's diag plugin (...EE01) and the mock's echo client (...EE02).
static const CLSID CLSID_WindowPlugin =
    { 0x7B6D1E44, 0x9C1A, 0x4C7E, { 0x9E, 0x2B, 0x11, 0xA0, 0xC0, 0xFF, 0xEE, 0x03 } };
static const IID IID_IWTSPlugin =
    { 0xA1230201, 0x1439, 0x4e62, { 0xa4, 0x14, 0x19, 0x0d, 0x0a, 0xc3, 0xd4, 0x0e } };

// ---- globals -------------------------------------------------------------------------------------
static HMODULE g_module = nullptr;
static LONG    g_objs = 0;
static LONG    g_locks = 0;
static HWND    g_mstsc = nullptr;
static std::wstring g_origTitle;
static HWND    g_overlay = nullptr;
static std::wstring g_overlayText;
static bool    g_overlayWanted = false;   // did a command ask for the chip? (still only shown when active)
static HANDLE  g_stop = nullptr;
static HANDLE  g_uiThread = nullptr;
static HANDLE  g_pipeThread = nullptr;
static HANDLE  g_attachThread = nullptr;

static const wchar_t* kMstscClass   = L"TscShellContainerClass";
static const wchar_t* kOverlayClass = L"RdpeekOverlay";
static const UINT WM_RDPEEK_OVERLAY = WM_APP + 1;   // wparam: 1 = show/update, 0 = hide

// Per-process control pipe, so a viewer can address one specific mstsc window in a multi-connection
// setup: \\.\pipe\rdpeek-window-<mstsc pid>. The Companion derives the same pid from the RDP window.
static std::wstring PipeName()
{
    wchar_t buf[64];
    swprintf_s(buf, L"\\\\.\\pipe\\rdpeek-window-%lu", GetCurrentProcessId());
    return buf;
}

static void Log(const char* fmt, ...)
{
    char path[MAX_PATH]; DWORD n = GetEnvironmentVariableA("TEMP", path, MAX_PATH);
    if (!n || n >= MAX_PATH) return;
    std::string p(path); p += "\\rdpeek-window-plugin.log";
    FILE* f = nullptr; if (fopen_s(&f, p.c_str(), "a") != 0 || !f) return;
    SYSTEMTIME t; GetLocalTime(&t);
    fprintf(f, "%02d:%02d:%02d.%03d  ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
    va_list a; va_start(a, fmt); vfprintf(f, fmt, a); va_end(a);
    fputc('\n', f); fclose(f);
}

static std::wstring Widen(const std::string& s)
{
    if (s.empty()) return L"";
    int w = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    std::wstring out((size_t)w, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), &out[0], w);
    return out;
}

// ---- find the session window ---------------------------------------------------------------------
static HWND FindMstscWindow()
{
    struct Ctx { DWORD pid; HWND found; } ctx{ GetCurrentProcessId(), nullptr };
    EnumWindows([](HWND h, LPARAM lp) -> BOOL {
        auto* c = reinterpret_cast<Ctx*>(lp);
        DWORD pid = 0; GetWindowThreadProcessId(h, &pid);
        if (pid != c->pid || !IsWindowVisible(h)) return TRUE;
        wchar_t cls[128] = {}; GetClassNameW(h, cls, 128);
        if (wcscmp(cls, kMstscClass) == 0) { c->found = h; return FALSE; }
        return TRUE;
    }, reinterpret_cast<LPARAM>(&ctx));
    return ctx.found;
}

// mstsc's fullscreen connection bar ("BBar") lives in the same process as a sibling window.
static HWND FindBBar()
{
    struct Ctx { DWORD pid; HWND found; } ctx{ GetCurrentProcessId(), nullptr };
    EnumWindows([](HWND h, LPARAM lp) -> BOOL {
        auto* c = reinterpret_cast<Ctx*>(lp);
        DWORD pid = 0; GetWindowThreadProcessId(h, &pid);
        if (pid != c->pid) return TRUE;
        wchar_t cls[64] = {}; GetClassNameW(h, cls, 64);
        if (wcscmp(cls, L"BBarWindowClass") == 0) { c->found = h; return FALSE; }
        return TRUE;
    }, reinterpret_cast<LPARAM>(&ctx));
    return ctx.found;
}

// Bind to mstsc's session window and remember its real title — exactly once. Callable from any
// thread; the first caller to see the window wins the original-title capture. We can't rely on
// IWTSPlugin::Connected for this: mstsc releases the plugin object right after Initialize (we never
// engage the channel manager), so Connected never fires — but our background threads live on and
// drive the window over the pipe. This is the reliable attach path.
static void EnsureAttached()
{
    if (g_mstsc && IsWindow(g_mstsc)) return;
    HWND h = FindMstscWindow();
    if (!h) return;
    g_mstsc = h;
    if (g_origTitle.empty()) {
        wchar_t t[512] = {}; GetWindowTextW(h, t, 512); g_origTitle = t;
    }
    Log("attached to mstsc HWND=%p title='%ls'", (void*)h, g_origTitle.c_str());
}

// ---- overlay (owned by the UI thread) -------------------------------------------------------------
static void PositionOverlay()
{
    if (!g_overlay || !g_mstsc || !IsWindow(g_mstsc)) return;
    RECT r; if (!GetWindowRect(g_mstsc, &r)) return;
    // A compact chip in the top-left that fits its text — an identity/status label, not a full bar.
    int w = 20 + (int)g_overlayText.size() * 8;
    if (w < 60) w = 60;
    int maxw = r.right - r.left; if (w > maxw) w = maxw;
    SetWindowPos(g_overlay, HWND_TOPMOST, r.left, r.top, w, 24, SWP_NOACTIVATE);   // show/hide is separate
}

// The chip belongs to one session: show it only when that session is the foreground window, so another
// session's topmost chip never bleeds over the one you're actually looking at.
static void EvalOverlay()
{
    if (!g_overlay) return;
    bool active = g_overlayWanted && g_mstsc && IsWindow(g_mstsc)
                  && !IsIconic(g_mstsc) && GetForegroundWindow() == g_mstsc;
    if (active)
    {
        PositionOverlay();
        if (!IsWindowVisible(g_overlay)) ShowWindow(g_overlay, SW_SHOWNA);
        InvalidateRect(g_overlay, nullptr, TRUE);
    }
    else if (IsWindowVisible(g_overlay))
    {
        ShowWindow(g_overlay, SW_HIDE);
    }
}

static LRESULT CALLBACK OverlayProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    switch (msg)
    {
    case WM_PAINT: {
        PAINTSTRUCT ps; HDC dc = BeginPaint(hwnd, &ps);
        RECT rc; GetClientRect(hwnd, &rc);
        HBRUSH bg = CreateSolidBrush(RGB(20, 24, 32));
        FillRect(dc, &rc, bg); DeleteObject(bg);
        SetBkMode(dc, TRANSPARENT); SetTextColor(dc, RGB(60, 220, 200));
        rc.left += 12;
        DrawTextW(dc, g_overlayText.c_str(), -1, &rc,
                  DT_SINGLELINE | DT_VCENTER | DT_LEFT | DT_END_ELLIPSIS);
        EndPaint(hwnd, &ps);
        return 0;
    }
    case WM_TIMER:            // periodic re-evaluation: follows focus between sessions
    case WM_RDPEEK_OVERLAY:   // immediate re-evaluation after a command
        EvalOverlay();
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

static DWORD WINAPI UiThread(LPVOID)
{
    WNDCLASSEXW wc = { sizeof(wc) };
    wc.lpfnWndProc = OverlayProc; wc.hInstance = g_module; wc.lpszClassName = kOverlayClass;
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    RegisterClassExW(&wc);

    // Create the overlay up front (hidden). Commands just show/hide/reposition it — so there's no
    // window-creation race and no reliance on thread-message dispatch.
    g_overlay = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED,
                                kOverlayClass, L"", WS_POPUP, 0, 0, 400, 26,
                                nullptr, nullptr, g_module, nullptr);
    if (g_overlay) { SetLayeredWindowAttributes(g_overlay, 0, 225, LWA_ALPHA); SetTimer(g_overlay, 1, 150, nullptr); }

    MSG m;
    while (GetMessageW(&m, nullptr, 0, 0) > 0) { TranslateMessage(&m); DispatchMessageW(&m); }
    return 0;
}

static void ShowOverlay(bool show) { g_overlayWanted = show; if (g_overlay) PostMessageW(g_overlay, WM_RDPEEK_OVERLAY, 0, 0); }

// Waits for mstsc's session window to appear (it doesn't exist yet at Initialize time), binds to it,
// captures its real title, and shows a proof-of-life HUD so the in-process load is visible without
// the Companion. After that the pipe drives everything.
static DWORD WINAPI AttachThread(LPVOID)
{
    // Just bind to the window. The HUD overlay is on-demand (the `overlay` command) — auto-showing a
    // banner across every session on connect was confusing, so proof-of-life is the log line instead.
    for (int i = 0; i < 150 && WaitForSingleObject(g_stop, 0) != WAIT_OBJECT_0; ++i) {
        EnsureAttached();
        if (g_mstsc) return 0;
        Sleep(200);
    }
    return 0;
}

// Pull a window to the foreground reliably. SetForegroundWindow alone is blocked by Windows' focus-
// steal lock when we aren't the foreground app; briefly attaching to the current foreground thread's
// input queue lifts that. Runs from inside mstsc, so this is our own window.
static void ForceForeground(HWND h)
{
    if (!h || !IsWindow(h)) return;
    if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
    HWND fg = GetForegroundWindow();
    DWORD fgTid = fg ? GetWindowThreadProcessId(fg, nullptr) : 0;
    DWORD myTid = GetCurrentThreadId();
    bool attached = fgTid && fgTid != myTid && AttachThreadInput(myTid, fgTid, TRUE);
    BringWindowToTop(h);
    SetForegroundWindow(h);
    SetActiveWindow(h);
    if (attached) AttachThreadInput(myTid, fgTid, FALSE);
}

// Does the window cover its monitor (i.e. mstsc is in fullscreen mode)? A small slack absorbs the
// off-by-a-pixel a fullscreen mstsc sometimes has, so we never misread fullscreen as windowed.
static bool CoversMonitor(HWND h)
{
    RECT wr;
    if (!h || !GetWindowRect(h, &wr)) return false;
    MONITORINFO mi; mi.cbSize = sizeof(mi);
    if (!GetMonitorInfo(MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST), &mi)) return false;
    const RECT& m = mi.rcMonitor;
    return wr.left <= m.left + 2 && wr.top <= m.top + 2 && wr.right >= m.right - 2 && wr.bottom >= m.bottom - 2;
}

// Toggle mstsc's fullscreen mode via its Ctrl+Alt+Break shortcut (Break == VK_CANCEL). It's a toggle
// and goes to the foreground window, so callers foreground the target first and only fire it when the
// window isn't already fullscreen — otherwise it would kick mstsc *out* of fullscreen.
static void SendFullscreenToggle()
{
    keybd_event(VK_CONTROL, 0, 0, 0);
    keybd_event(VK_MENU, 0, 0, 0);
    keybd_event(VK_CANCEL, 0, 0, 0);
    Sleep(30);
    keybd_event(VK_CANCEL, 0, KEYEVENTF_KEYUP, 0);
    keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);
    keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
}

// ---- command execution ---------------------------------------------------------------------------
static void Execute(const std::string& line)
{
    Log("cmd: %s", line.c_str());
    EnsureAttached();
    if (!g_mstsc) { Log("  (no mstsc window)"); return; }

    auto sp = line.find(' ');
    std::string verb = line.substr(0, sp);
    std::string arg  = (sp == std::string::npos) ? "" : line.substr(sp + 1);

    if (verb == "title") {
        std::wstring t = g_origTitle;
        if (!arg.empty()) { t += L"  —  "; t += Widen(arg); }
        SetWindowTextW(g_mstsc, t.c_str());
    } else if (verb == "move") {
        int x, y, w, h;
        if (sscanf_s(arg.c_str(), "%d %d %d %d", &x, &y, &w, &h) == 4)
            SetWindowPos(g_mstsc, nullptr, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
    } else if (verb == "topmost") {
        SetWindowPos(g_mstsc, arg == "on" ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
                     SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    } else if (verb == "show") {
        ShowWindow(g_mstsc, arg == "min" ? SW_MINIMIZE : arg == "max" ? SW_MAXIMIZE : SW_RESTORE);
    } else if (verb == "flash") {
        FLASHWINFO fi = { sizeof(fi), g_mstsc, FLASHW_ALL, 3, 0 }; FlashWindowEx(&fi);
    } else if (verb == "bbar") {
        if (HWND b = FindBBar()) ShowWindow(b, arg == "hide" ? SW_HIDE : SW_SHOW);
    } else if (verb == "foreground") {
        ForceForeground(g_mstsc);           // used by the Companion's switch-window hotkey
    } else if (verb == "fullscreen") {
        // Switch to this session AND make sure it's fullscreen. Foreground first (so the toggle keys
        // reach it), then enter fullscreen only if it isn't already covering the monitor. Poll for
        // coverage so a session still animating back from minimized-fullscreen isn't misread as
        // windowed and toggled straight back out.
        ForceForeground(g_mstsc);
        bool full = false;
        for (int i = 0; i < 8 && !(full = CoversMonitor(g_mstsc)); ++i) Sleep(60);
        if (!full) SendFullscreenToggle();
    } else if (verb == "overlay") {
        g_overlayText = arg.empty() ? L"" : Widen(arg);
        ShowOverlay(!arg.empty());
    }
}

// ---- control pipe --------------------------------------------------------------------------------
static DWORD WINAPI PipeThread(LPVOID)
{
    std::wstring name = PipeName();
    Log("pipe: listening on %ls", name.c_str());
    while (WaitForSingleObject(g_stop, 0) != WAIT_OBJECT_0)
    {
        HANDLE pipe = CreateNamedPipeW(name.c_str(), PIPE_ACCESS_INBOUND,
            PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT, 1, 0, 4096, 0, nullptr);
        if (pipe == INVALID_HANDLE_VALUE) { Sleep(500); continue; }
        if (!ConnectNamedPipe(pipe, nullptr) && GetLastError() != ERROR_PIPE_CONNECTED) {
            CloseHandle(pipe); continue;
        }
        char buf[4096]; DWORD read = 0;
        while (ReadFile(pipe, buf, sizeof(buf) - 1, &read, nullptr) && read > 0) {
            std::string s(buf, read);
            size_t start = 0;
            while (start < s.size()) {
                size_t nl = s.find_first_of("\r\n", start);
                std::string line = s.substr(start, nl == std::string::npos ? std::string::npos : nl - start);
                if (!line.empty()) Execute(line);
                if (nl == std::string::npos) break;
                start = nl + 1;
            }
        }
        DisconnectNamedPipe(pipe); CloseHandle(pipe);
    }
    return 0;
}

static void StartThreads()
{
    if (!g_stop) g_stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!g_uiThread)     g_uiThread     = CreateThread(nullptr, 0, UiThread,     nullptr, 0, nullptr);
    if (!g_pipeThread)   g_pipeThread   = CreateThread(nullptr, 0, PipeThread,   nullptr, 0, nullptr);
    if (!g_attachThread) g_attachThread = CreateThread(nullptr, 0, AttachThread, nullptr, 0, nullptr);
}

// ---- the plugin object (IWTSPlugin: IUnknown + Initialize/Connected/Disconnected/Terminated) ------
class WindowPlugin : public IUnknown
{
    LONG m_ref = 1;
public:
    WindowPlugin() { InterlockedIncrement(&g_objs); }
    virtual ~WindowPlugin() { InterlockedDecrement(&g_objs); }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IWTSPlugin) { *ppv = this; AddRef(); return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&m_ref); }
    ULONG STDMETHODCALLTYPE Release() override { LONG r = InterlockedDecrement(&m_ref); if (!r) delete this; return r; }

    virtual HRESULT STDMETHODCALLTYPE Initialize(void* /*mgr*/) {
        // The one lifecycle callback we actually get: mstsc releases us right after this (we create no
        // channel listener), so Connected/Disconnected/Terminated below generally never fire. The
        // factory already started the background threads; AttachThread binds to the window from here.
        Log("Initialize — in-process, pid=%lu", GetCurrentProcessId());
        return S_OK;
    }
    virtual HRESULT STDMETHODCALLTYPE Connected() {   // best-effort: rarely called (see Initialize)
        Log("Connected");
        EnsureAttached();
        return S_OK;
    }
    virtual HRESULT STDMETHODCALLTYPE Disconnected(DWORD code) { Log("Disconnected %lu", code); Restore(); return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE Terminated() { Log("Terminated"); Restore(); if (g_stop) SetEvent(g_stop); return S_OK; }

    static void Restore() {
        if (g_mstsc && IsWindow(g_mstsc) && !g_origTitle.empty()) SetWindowTextW(g_mstsc, g_origTitle.c_str());
        ShowOverlay(false);
    }
};

// ---- class factory --------------------------------------------------------------------------------
class Factory : public IClassFactory
{
    LONG m_ref = 1;
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IClassFactory) { *ppv = this; AddRef(); return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&m_ref); }
    ULONG STDMETHODCALLTYPE Release() override { LONG r = InterlockedDecrement(&m_ref); if (!r) delete this; return r; }

    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override {
        if (outer) return CLASS_E_NOAGGREGATION;
        StartThreads();
        auto* p = new (std::nothrow) WindowPlugin();
        if (!p) return E_OUTOFMEMORY;
        HRESULT hr = p->QueryInterface(riid, ppv);
        p->Release();
        return hr;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override {
        if (lock) InterlockedIncrement(&g_locks); else InterlockedDecrement(&g_locks); return S_OK;
    }
};

// ---- DLL exports ----------------------------------------------------------------------------------
extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    Log("DllGetClassObject — pid=%lu, our clsid=%d", GetCurrentProcessId(), rclsid == CLSID_WindowPlugin);
    if (rclsid != CLSID_WindowPlugin) return CLASS_E_CLASSNOTAVAILABLE;
    auto* f = new (std::nothrow) Factory();
    if (!f) return E_OUTOFMEMORY;
    HRESULT hr = f->QueryInterface(riid, ppv);
    f->Release();
    return hr;
}

// We spin background threads (UI + pipe) that outlive the COM objects, so never let mstsc unload the
// DLL out from under them — it stays mapped until the process exits. A plugin DLL doing this is fine.
extern "C" HRESULT __stdcall DllCanUnloadNow() { return S_FALSE; }

BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) { g_module = h; DisableThreadLibraryCalls(h); Log("DllMain ATTACH — pid=%lu", GetCurrentProcessId()); }
    return TRUE;
}

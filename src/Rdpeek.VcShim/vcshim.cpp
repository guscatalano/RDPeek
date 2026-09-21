// rdpeek-vc-shim — a tiny native DVC plugin DLL.
//
// Two ways exist to load a client DVC plugin into an RDP session:
//   1. mstsc.exe reads the Terminal Server Client "AddIns" registry and activates a LocalServer32
//      COM object (IWTSPlugin) by CLSID. This is how RDPeek's plugin ships.
//   2. A *hosted* mstscax control (e.g. the Rdpeek.Bootstrap probe) ignores those AddIns; it only
//      loads plugin DLLs named in its `PluginDlls` advanced setting and calls the DLL's exported
//      VirtualChannelGetInstance to obtain the IWTSPlugin (the DLL-export model, MS-RDPEDYC).
//
// This shim bridges (2) -> RDPeek's existing (1): its VirtualChannelGetInstance export simply
// CoCreateInstance's the already-registered RDPeek COM plugin and hands the client that same
// IWTSPlugin. The plugin itself is unchanged and still runs out-of-process (LocalServer32), so a
// plugin crash still can't take down the host. Point a hosted control's PluginDlls at this DLL and
// the plugin loads headlessly — no mstsc.exe required.

#include <windows.h>
#include <unknwn.h>

// Must match PluginHost.ClsidString / tools/register.ps1 (the RDPeek COM plugin) ...
static const CLSID CLSID_Rdpeek =
    { 0x7B6D1E44, 0x9C1A, 0x4C7E, { 0x9E, 0x2B, 0x11, 0xA0, 0xC0, 0xFF, 0xEE, 0x01 } };
// ... and IWTSPlugin from ComInterfaces.cs (also what mstsc passes us as refiid).
static const IID IID_IWTSPlugin =
    { 0xA1230201, 0x1439, 0x4e62, { 0xa4, 0x14, 0x19, 0x0d, 0x0a, 0xc3, 0xd4, 0x0e } };

// MS-RDPEDYC plugin entry point. The client calls once with ppObjArray == null to learn the count,
// then again to receive the instance(s). We advertise exactly one plugin.
extern "C" __declspec(dllexport)
HRESULT __stdcall VirtualChannelGetInstance(REFIID refiid, ULONG* pNumObjs, VOID** ppObjArray)
{
    if (pNumObjs == nullptr)
        return E_INVALIDARG;

    if (ppObjArray == nullptr)      // count query
    {
        *pNumObjs = 1;
        return S_OK;
    }

    // MTA, matching RDPeek's plugin host. Ignore RPC_E_CHANGED_MODE — the host may already have
    // initialised COM on this thread; we only need it initialised, not to own it.
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);

    IUnknown* plugin = nullptr;
    // Prefer the registered LocalServer32 (out-of-process) but allow in-proc as a fallback. Forward
    // the caller's refiid so we return precisely the interface it asked for (IWTSPlugin).
    HRESULT hr = CoCreateInstance(CLSID_Rdpeek, nullptr,
        CLSCTX_LOCAL_SERVER | CLSCTX_INPROC_SERVER, refiid, reinterpret_cast<void**>(&plugin));
    if (FAILED(hr))
    {
        *pNumObjs = 0;
        return hr;
    }

    *pNumObjs = 1;
    ppObjArray[0] = plugin;         // ownership transfers to the client (it Release()s later)
    return S_OK;
}

BOOL WINAPI DllMain(HINSTANCE hinst, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
        DisableThreadLibraryCalls(hinst);   // hinst is this DLL's own module handle
    return TRUE;
}

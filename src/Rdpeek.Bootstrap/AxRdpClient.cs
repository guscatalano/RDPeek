using Microsoft.Win32;
using System.Windows.Forms;

namespace Rdpeek.Bootstrap;

/// <summary>
/// Minimal WinForms host for the RDP ActiveX control (mstscax.dll).
///
/// We hand-roll the <see cref="AxHost"/> shell instead of shipping aximp-generated
/// wrappers, and drive the control through IDispatch late binding (see
/// <see cref="BootstrapForm"/>) rather than strongly-typed MSTSCLib interfaces.
///
/// It resolves the <b>NotSafeForScripting</b> coclass on purpose: the scripting-safe
/// <c>MsTscAx.MsTscAx</c> control restricts full-trust features (redirection, PluginDlls),
/// so it is the wrong host for provisioning. The NotSafeForScripting control is what
/// mstsc-class hosts use (cf. NexusRDM.RdpAx).
///
/// IMPORTANT (verified 2026-09-20): even this control does NOT load the client DVC COM
/// AddIns that mstsc.exe activates from the Terminal Server Client "AddIns" registry — so a
/// registered RDPeek COM plugin never loads into it on its own. To load a plugin headlessly,
/// set the control's <c>PluginDlls</c> to a DVC plugin DLL exporting VirtualChannelGetInstance
/// (rdpeek-vc-shim.dll bridges that export to the registered RDPeek COM plugin).
///
/// Connection state is polled via the control's <c>Connected</c> property rather than
/// sunk through <c>IMsTscAxEvents</c>.
/// </summary>
internal sealed class AxRdpClient : AxHost
{
    public AxRdpClient() : base(ResolveClsid())
    {
    }

    /// <summary>The underlying RDP control (an IDispatch COM object). Valid only once the
    /// control's window handle — and thus the OCX — exists, i.e. after the host form has
    /// been shown. Use <c>dynamic</c> to script it.</summary>
    public object Control => GetOcx();

    private static string ResolveClsid()
    {
        // MsRdpClient9NotSafeForScripting — the CLSID NexusRDM.RdpAx uses: broadest modern
        // compatibility, exposes AdvancedSettings9, and (unlike the scripting-safe control)
        // loads registered DVC plugins.
        const string notSafeV9 = "{A41A4187-5A86-4E26-B40A-856F9035D9CB}";

        // Prefer NotSafeForScripting by ProgID when registered (newest first)…
        string[] progIds =
        {
            "MsRdpClient12NotSafeForScripting",
            "MsRdpClient11NotSafeForScripting",
            "MsRdpClient10NotSafeForScripting",
            "MsRdpClient9NotSafeForScripting",
        };
        foreach (var progId in progIds)
        {
            var t = Type.GetTypeFromProgID(progId);
            if (t is not null) return t.GUID.ToString("B");
        }

        // …else the known NotSafeForScripting CLSID (its ProgID often isn't registered)…
        if (IsClsidRegistered(notSafeV9)) return notSafeV9;

        // …else fall back to the scripting-safe control (connects, but may not load plugins).
        var fallback = Type.GetTypeFromProgID("MsTscAx.MsTscAx");
        if (fallback is not null) return fallback.GUID.ToString("B");

        throw new InvalidOperationException(
            "The RDP ActiveX control (mstscax.dll) is not registered on this machine.");
    }

    private static bool IsClsidRegistered(string clsid)
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}\InprocServer32");
        return key is not null;
    }
}

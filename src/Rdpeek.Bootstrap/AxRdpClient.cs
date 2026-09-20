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
/// <c>MsTscAx.MsTscAx</c> control restricts full-trust features — including loading
/// third-party DVC plugins (AddIns) — so a registered RDPeek client plugin never loads
/// into it. The NotSafeForScripting control is what mstsc-class hosts use (cf.
/// NexusRDM.RdpAx), and it loads AddIns like mstsc.exe does.
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

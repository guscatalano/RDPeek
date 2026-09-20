using System.Windows.Forms;

namespace Rdpeek.Bootstrap;

/// <summary>
/// Minimal WinForms host for the RDP ActiveX control (mstscax.dll).
///
/// We hand-roll the <see cref="AxHost"/> shell instead of shipping aximp-generated
/// wrappers, and drive the control through IDispatch late binding (see
/// <see cref="BootstrapForm"/>) rather than strongly-typed MSTSCLib interfaces —
/// the control's dual interfaces resolve every member by name at runtime, which
/// sidesteps the member-layout differences between interface generations.
///
/// Connection state is polled via the control's <c>Connected</c> property rather
/// than sunk through <c>IMsTscAxEvents</c>: enough to narrate a one-shot
/// provisioning connection without the dispinterface connection-point plumbing.
/// </summary>
internal sealed class AxRdpClient : AxHost
{
    public AxRdpClient() : base(ResolveClsid())
    {
    }

    /// <summary>
    /// The underlying RDP control (an IDispatch COM object). Valid only once the
    /// control's window handle — and thus the OCX — exists, i.e. after the host
    /// form has been shown. Use <c>dynamic</c> to script it.
    /// </summary>
    public object Control => GetOcx();

    /// <summary>
    /// The control registers version-independently as <c>MsTscAx.MsTscAx</c>
    /// (newest installed); fall back through explicit generations for older or
    /// locked-down machines.
    /// </summary>
    private static string ResolveClsid()
    {
        string[] progIds =
        {
            "MsTscAx.MsTscAx",       // version-independent -> newest installed
            "MsTscAx.MsTscAx.13",
            "MsTscAx.MsTscAx.12",
            "MsTscAx.MsTscAx.11",
            "MsTscAx.MsTscAx.10",
            "MsTscAx.MsTscAx.9",
            "MsTscAx.MsTscAx.6",
        };

        foreach (var progId in progIds)
        {
            var type = Type.GetTypeFromProgID(progId);
            if (type is not null)
                return type.GUID.ToString("B");
        }

        throw new InvalidOperationException(
            "The RDP ActiveX control (mstscax.dll) is not registered on this machine.");
    }
}

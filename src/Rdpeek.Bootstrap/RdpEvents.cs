using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Rdpeek.Bootstrap;

/// <summary>
/// The RDP ActiveX control's outgoing event interface (IMsTscAxEvents), declared as an IDispatch
/// sink so we can Advise it through a connection point and hear why a connection failed. We only
/// declare the events we care about; the control dispatches by DISPID, and the unlisted ones are
/// simply not routed. <see cref="OnDisconnected"/> carries the disconnect reason code that
/// <c>ExtendedDisconnectReason</c> (a property) leaves at 0 for a pre-login abort.
/// </summary>
[ComImport, Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6"),
 InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IMsTscAxEvents
{
    [DispId(1)] void OnConnecting();
    [DispId(2)] void OnConnected();
    [DispId(3)] void OnLoginComplete();
    [DispId(4)] void OnDisconnected(int discReason);
    [DispId(10)] void OnFatalError(int errorCode);
    [DispId(11)] void OnWarning(int warningCode);
    [DispId(16)] void OnReceivedTSPublicKey(string publicKey, ref bool pfContinueLogon);
    [DispId(18)] void OnAuthenticationWarningDisplayed();
}

/// <summary>
/// A logging sink for the RDP control's events. Advise it through the control's
/// IConnectionPointContainer before Connect(); it prints each connect-lifecycle event so a failed
/// connect names its reason instead of just "timed out". <paramref name="describe"/> turns a
/// disconnect reason code into the control's own human text (via GetErrorDescription).
/// </summary>
internal sealed class RdpEventSink : IMsTscAxEvents
{
    private readonly Func<int, string> _describe;
    public RdpEventSink(Func<int, string> describe) => _describe = describe;

    public void OnConnecting() => Console.WriteLine("event: OnConnecting");
    public void OnConnected() => Console.WriteLine("event: OnConnected");
    public void OnLoginComplete() => Console.WriteLine("event: OnLoginComplete");

    public void OnDisconnected(int discReason)
    {
        string desc = "";
        try { desc = _describe(discReason); } catch { }
        Console.WriteLine($"event: OnDisconnected discReason={discReason} (0x{discReason:X}) {desc}".TrimEnd());
    }

    public void OnFatalError(int errorCode) => Console.WriteLine($"event: OnFatalError errorCode={errorCode}");
    public void OnWarning(int warningCode) => Console.WriteLine($"event: OnWarning warningCode={warningCode}");
    public void OnReceivedTSPublicKey(string publicKey, ref bool pfContinueLogon)
    {
        Console.WriteLine("event: OnReceivedTSPublicKey — continuing logon");
        pfContinueLogon = true;
    }
    public void OnAuthenticationWarningDisplayed() => Console.WriteLine("event: OnAuthenticationWarningDisplayed");

    /// <summary>Advise this sink on the control's IMsTscAxEvents connection point. Returns the cookie
    /// to Unadvise with, or 0 if the control exposes no such connection point.</summary>
    public static int Advise(object control, RdpEventSink sink, out IConnectionPoint? cp)
    {
        cp = null;
        try
        {
            var cpc = (IConnectionPointContainer)control;
            var iid = typeof(IMsTscAxEvents).GUID;
            cpc.FindConnectionPoint(ref iid, out cp);
            cp.Advise(sink, out int cookie);
            return cookie;
        }
        catch (Exception ex)
        {
            Console.WriteLine("could not advise RDP event sink: " + ex.Message);
            return 0;
        }
    }
}

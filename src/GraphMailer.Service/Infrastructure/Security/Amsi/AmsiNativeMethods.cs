using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GraphMailer.Service.Infrastructure.Security.Amsi;

/// <summary>
/// Results returned by the AMSI scan functions (<c>AMSI_RESULT</c> in amsi.h).
/// Anything at or above <see cref="Detected"/> means the content must be blocked;
/// the <c>BLOCKED_BY_ADMIN_*</c> range is an administrative block, not a signature hit,
/// but is equally binding. Mirrors the <c>AmsiResultIsMalware</c> macro, which has no
/// exported counterpart and therefore has to be reimplemented here.
/// </summary>
internal static class AmsiResult
{
    internal const uint Clean = 0;
    internal const uint NotDetected = 1;
    internal const uint BlockedByAdminStart = 16384;
    internal const uint BlockedByAdminEnd = 20479;
    internal const uint Detected = 32768;

    /// <summary>The <c>AmsiResultIsMalware</c> macro: block everything from the admin range upwards.</summary>
    internal static bool IsMalware(uint result) => result >= BlockedByAdminStart;
}

/// <summary>
/// Handle returned by <c>AmsiInitialize</c>. Released with <c>AmsiUninitialize</c>, which
/// takes no result and cannot fail — hence the unconditional <see langword="true"/>.
/// </summary>
internal sealed class AmsiContextHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal AmsiContextHandle() : base(ownsHandle: true) { }

    protected override bool ReleaseHandle()
    {
        AmsiNativeMethods.AmsiUninitialize(handle);
        return true;
    }
}

/// <summary>
/// P/Invoke surface for <c>amsi.dll</c> (Windows 10 / Server 2016 and later).
///
/// Deliberately the flat Win32 API rather than the <c>IAntimalware</c>/<c>IAmsiStream</c> COM
/// interfaces: streaming would only pay off for content larger than a single buffer, and
/// <c>Smtp.MaxSizeBytes</c> caps a message well below that. The COM route would cost a full
/// interop implementation for no gain here.
///
/// Note that a scan never yields a threat *name* — the only output is the numeric
/// <see cref="AmsiResult"/>. The name exists solely inside the antimalware product
/// (for Defender: event 1116 / <c>Get-MpThreat</c>), so nothing downstream can report it.
/// </summary>
internal static class AmsiNativeMethods
{
    private const string AmsiDll = "amsi.dll";

    /// <summary>S_OK.</summary>
    internal const int Ok = 0;

    [DllImport(AmsiDll, CharSet = CharSet.Unicode)]
    internal static extern int AmsiInitialize(string appName, out AmsiContextHandle amsiContext);

    [DllImport(AmsiDll)]
    internal static extern void AmsiUninitialize(IntPtr amsiContext);

    [DllImport(AmsiDll)]
    internal static extern int AmsiOpenSession(AmsiContextHandle amsiContext, out IntPtr amsiSession);

    [DllImport(AmsiDll)]
    internal static extern void AmsiCloseSession(AmsiContextHandle amsiContext, IntPtr amsiSession);

    /// <param name="length">
    /// Bytes to read from <paramref name="buffer"/>. Allows scanning a prefix of a pooled or
    /// oversized array without copying it down to size first.
    /// </param>
    /// <param name="contentName">
    /// Filename or similar. Providers use it as a hint (extension, type), so passing the real
    /// attachment name measurably improves detection over a generic label.
    /// </param>
    [DllImport(AmsiDll, CharSet = CharSet.Unicode)]
    internal static extern int AmsiScanBuffer(
        AmsiContextHandle amsiContext,
        [In] byte[] buffer,
        uint length,
        string contentName,
        IntPtr amsiSession,
        out uint result);
}

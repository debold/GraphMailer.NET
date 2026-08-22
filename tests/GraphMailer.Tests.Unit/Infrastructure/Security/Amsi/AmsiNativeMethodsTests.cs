using System.Reflection;
using System.Runtime.InteropServices;
using GraphMailer.Service.Infrastructure.Security.Amsi;

namespace GraphMailer.Tests.Unit.Infrastructure.Security.Amsi;

/// <summary>
/// Guards the DLL search path of the AMSI interop. <c>amsi.dll</c> is not a KnownDLL, so without
/// <see cref="DefaultDllImportSearchPathsAttribute"/> the loader would probe the application
/// directory before <c>System32</c> — a planted copy would disable the malware scan *and* execute
/// as SYSTEM inside the service. The attribute is invisible at runtime until it is missing, and it
/// has to be repeated per method (it is not valid on a type), so the test walks every import
/// instead of checking one spot.
/// </summary>
public sealed class AmsiNativeMethodsTests
{
    private static IEnumerable<MethodInfo> Imports() =>
        typeof(AmsiNativeMethods)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.GetCustomAttribute<DllImportAttribute>() is not null);

    [Fact]
    public void EveryAmsiImport_PinsTheDllSearchPathToSystem32()
    {
        Imports().Should().NotBeEmpty("the reflection filter must actually find the imports");

        foreach (var method in Imports())
        {
            var attribute = method.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>();

            attribute.Should().NotBeNull(
                "{0} would otherwise resolve amsi.dll from the application directory", method.Name);
            attribute!.Paths.Should().Be(DllImportSearchPath.System32, "checked for {0}", method.Name);
        }
    }
}

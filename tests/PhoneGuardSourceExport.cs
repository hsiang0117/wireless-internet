using System.IO;
using System.Text;
using WirelessInternet;

internal static class PhoneGuardSourceExport
{
    private static int Main(string[] args)
    {
        File.WriteAllText(args[0], PhoneGuardSource.Text.Replace("\r\n", "\n"), new UTF8Encoding(false));
        return 0;
    }
}

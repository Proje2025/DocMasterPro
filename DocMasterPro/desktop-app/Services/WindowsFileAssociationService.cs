using System.IO;
using Microsoft.Win32;

namespace DocConverter.Services;

public static class WindowsFileAssociationService
{
    private const string AppName = "DocMaster Pro";
    private const string ExeName = "DocConverter.exe";
    private const string ProgId = "DocMasterPro.PDF";
    private const string CapabilitiesPath = @"Software\DocMasterPro\Capabilities";
    private const string RegisteredApplicationName = "DocMaster Pro";

    public static void RegisterPdfOpenWithForCurrentUser()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string exePath = Environment.ProcessPath ?? "";
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return;

        try
        {
            RegisterPdfOpenWithForCurrentUser(exePath);
        }
        catch (Exception ex)
        {
            FileLogger.LogError("PdfOpenWithRegistration", ex);
        }
    }

    internal static void RegisterPdfOpenWithForCurrentUser(string exePath)
    {
        string fullExePath = Path.GetFullPath(exePath);
        string openCommand = $"\"{fullExePath}\" \"%1\"";

        using (RegistryKey progIdKey = CreateCurrentUserSubKey($@"Software\Classes\{ProgId}"))
        {
            progIdKey.SetValue("", "DocMaster Pro PDF Document", RegistryValueKind.String);
        }

        using (RegistryKey defaultIconKey = CreateCurrentUserSubKey($@"Software\Classes\{ProgId}\DefaultIcon"))
        {
            defaultIconKey.SetValue("", $"{fullExePath},0", RegistryValueKind.String);
        }

        using (RegistryKey commandKey = CreateCurrentUserSubKey($@"Software\Classes\{ProgId}\shell\open\command"))
        {
            commandKey.SetValue("", openCommand, RegistryValueKind.String);
        }

        using (RegistryKey openWithProgIdsKey = CreateCurrentUserSubKey(@"Software\Classes\.pdf\OpenWithProgids"))
        {
            openWithProgIdsKey.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.Binary);
        }

        CreateCurrentUserSubKey($@"Software\Classes\.pdf\OpenWithList\{ExeName}").Dispose();

        using (RegistryKey applicationKey = CreateCurrentUserSubKey($@"Software\Classes\Applications\{ExeName}"))
        {
            applicationKey.SetValue("FriendlyAppName", AppName, RegistryValueKind.String);
        }

        using (RegistryKey applicationCommandKey = CreateCurrentUserSubKey($@"Software\Classes\Applications\{ExeName}\shell\open\command"))
        {
            applicationCommandKey.SetValue("", openCommand, RegistryValueKind.String);
        }

        using (RegistryKey supportedTypesKey = CreateCurrentUserSubKey($@"Software\Classes\Applications\{ExeName}\SupportedTypes"))
        {
            supportedTypesKey.SetValue(".pdf", "", RegistryValueKind.String);
        }

        using (RegistryKey capabilitiesKey = CreateCurrentUserSubKey(CapabilitiesPath))
        {
            capabilitiesKey.SetValue("ApplicationName", AppName, RegistryValueKind.String);
            capabilitiesKey.SetValue("ApplicationDescription", "Open PDF files with DocMaster Pro PDF Studio.", RegistryValueKind.String);
            capabilitiesKey.SetValue("ApplicationIcon", $"{fullExePath},0", RegistryValueKind.String);
        }

        using (RegistryKey fileAssociationsKey = CreateCurrentUserSubKey($@"{CapabilitiesPath}\FileAssociations"))
        {
            fileAssociationsKey.SetValue(".pdf", ProgId, RegistryValueKind.String);
        }

        using (RegistryKey appPathsKey = CreateCurrentUserSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{ExeName}"))
        {
            appPathsKey.SetValue("", fullExePath, RegistryValueKind.String);
        }

        using (RegistryKey registeredApplicationsKey = CreateCurrentUserSubKey(@"Software\RegisteredApplications"))
        {
            registeredApplicationsKey.SetValue(RegisteredApplicationName, CapabilitiesPath, RegistryValueKind.String);
        }
    }

    private static RegistryKey CreateCurrentUserSubKey(string subkey)
    {
        return Registry.CurrentUser.CreateSubKey(subkey, writable: true)
            ?? throw new InvalidOperationException($"Registry key could not be created: HKCU\\{subkey}");
    }
}

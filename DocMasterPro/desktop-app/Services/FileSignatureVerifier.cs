using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DocConverter.Services
{
    /// <summary>İndirilen bir dosyanın dijital imzasını ve yayıncısını denetler.</summary>
    public interface IFileSignatureVerifier
    {
        /// <summary>
        /// Dosyanın Windows'un güvendiği geçerli bir imzası varsa ve imzalayan <paramref name="expectedPublisher"/>
        /// ise true döner. İmzasız, bozulmuş ya da başka bir yayıncıya ait dosyada false döner; hata fırlatmaz.
        /// </summary>
        bool IsSignedBy(string filePath, string expectedPublisher);
    }

    /// <summary>
    /// Authenticode doğrulaması: imzanın geçerliliğine Windows (WinVerifyTrust) karar verir; ardından imzalayan
    /// sertifikanın adı beklenen yayıncıyla karşılaştırılır. Zaman damgalı imzalar, sertifikanın süresi sonradan
    /// dolmuş olsa da geçerlidir.
    /// </summary>
    public sealed class AuthenticodeVerifier : IFileSignatureVerifier
    {
        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        private const uint UiNone = 2;
        private const uint RevokeNone = 0;
        private const uint ChoiceFile = 1;
        private const uint StateActionVerify = 1;
        private const uint StateActionClose = 2;
        private const uint ProviderFlagRevocationCheckNone = 0x10;

        public bool IsSignedBy(string filePath, string expectedPublisher)
        {
            if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(expectedPublisher))
                return false;

            try
            {
                if (!HasTrustedSignature(filePath))
                    return false;

                using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
                string signer = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
                return string.Equals(signer, expectedPublisher, StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is CryptographicException or System.IO.IOException or UnauthorizedAccessException)
            {
                FileLogger.LogError("İmza doğrulanamadı", ex);
                return false;
            }
        }

        private static bool HasTrustedSignature(string filePath)
        {
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = filePath,
            };

            IntPtr fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            IntPtr dataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            try
            {
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);

                var data = new WinTrustData
                {
                    StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                    UiChoice = UiNone,
                    // Dosyanın kendisi sabit bir SHA-256 özetiyle de denetlenir; iptal listesi sorgusu kurumsal
                    // ağlarda sıkça engellendiği için yapılmaz.
                    RevocationChecks = RevokeNone,
                    UnionChoice = ChoiceFile,
                    FileInfo = fileInfoPointer,
                    StateAction = StateActionVerify,
                    ProviderFlags = ProviderFlagRevocationCheckNone,
                };
                Marshal.StructureToPtr(data, dataPointer, fDeleteOld: false);

                int result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, dataPointer);

                // Doğrulama için ayrılan durum verisini serbest bırak.
                data = Marshal.PtrToStructure<WinTrustData>(dataPointer);
                data.StateAction = StateActionClose;
                Marshal.StructureToPtr(data, dataPointer, fDeleteOld: false);
                WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, dataPointer);

                return result == 0;
            }
            finally
            {
                Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
                Marshal.FreeHGlobal(fileInfoPointer);
                Marshal.FreeHGlobal(dataPointer);
            }
        }

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr window, [MarshalAs(UnmanagedType.LPStruct)] Guid action, IntPtr data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint StructSize;
            [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint StructSize;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr FileInfo;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProviderFlags;
            public uint UiContext;
            public IntPtr SignatureSettings;
        }
    }
}

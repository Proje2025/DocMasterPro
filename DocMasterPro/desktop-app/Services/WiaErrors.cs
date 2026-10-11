using System;
using System.Runtime.InteropServices;

namespace DocConverter.Services
{
    /// <summary>
    /// Bir WIA aktarım hatasından sonra ne yapılacağı.
    /// </summary>
    public enum WiaFailureKind
    {
        /// <summary>Besleyicide kağıt kalmadı. İlk sayfadan sonra taramanın normal bitişidir.</summary>
        PaperEmpty,

        /// <summary>Kullanıcı taramayı iptal etti.</summary>
        Cancelled,

        /// <summary>Cihaz meşgul, ısınıyor, uykuda ya da bağlantı henüz kurulmadı: yeniden denenebilir.</summary>
        Transient,

        /// <summary>Kullanıcının müdahalesi gerekiyor (kağıt sıkışması, kapak açık...). Yeniden denemek anlamsız.</summary>
        NeedsUser
    }

    /// <summary>
    /// WIA HRESULT kodlarını sınıflandırır ve kullanıcıya gösterilecek Türkçe açıklamaya çevirir.
    /// </summary>
    public static class WiaErrors
    {
        public const uint PaperJam = 0x80210002;
        public const uint PaperEmpty = 0x80210003;
        public const uint PaperProblem = 0x80210004;
        public const uint Offline = 0x80210005;
        public const uint Busy = 0x80210006;
        public const uint WarmingUp = 0x80210007;
        public const uint UserIntervention = 0x80210008;
        public const uint DeviceCommunication = 0x8021000A;
        public const uint DeviceLocked = 0x8021000D;
        public const uint CoverOpen = 0x80210010;
        public const uint LampOff = 0x80210011;
        public const uint MultiFeed = 0x80210014;
        public const uint UserCancelled = 0x80210064;

        public static uint CodeOf(Exception ex) => unchecked((uint)ex.HResult);

        public static WiaFailureKind Classify(Exception ex) => Classify(CodeOf(ex));

        public static WiaFailureKind Classify(uint code) => code switch
        {
            PaperEmpty => WiaFailureKind.PaperEmpty,
            UserCancelled => WiaFailureKind.Cancelled,

            PaperJam or PaperProblem or UserIntervention or MultiFeed or CoverOpen or LampOff
                // Bazı sürücüler kapak/lamba için 0x16 / 0x17 döndürür.
                or 0x80210016 or 0x80210017 => WiaFailureKind.NeedsUser,

            // Geri kalan her şey (meşgul, ısınıyor, çevrimdışı, iletişim, sürücü istisnası, RPC,
            // bilinmeyen kodlar) geçici sayılır: aynı işlem ikinci denemede çoğunlukla başarılı olur.
            _ => WiaFailureKind.Transient
        };

        public static string Describe(Exception ex)
        {
            uint code = CodeOf(ex);
            string text = code switch
            {
                PaperJam => "Kağıt sıkıştı. Besleyiciyi açıp sıkışan kağıdı çıkarın.",
                PaperEmpty => "Besleyicide kağıt algılanmadı. Belgeleri besleyiciye yerleştirin.",
                PaperProblem => "Besleyicide kağıt sorunu var. Kağıtları düzeltip yeniden yerleştirin.",
                Offline => "Tarayıcı çevrimdışı veya uyku modunda. Açık ve bağlı olduğunu kontrol edin.",
                Busy => "Tarayıcı meşgul. Başka bir program (PaperStream, ScanSnap vb.) tarayıcıyı kullanıyor olabilir.",
                WarmingUp => "Tarayıcı ısınıyor. Birkaç saniye sonra yeniden deneyin.",
                UserIntervention => "Tarayıcı müdahale bekliyor. Cihaz üzerindeki uyarıyı kontrol edin.",
                DeviceCommunication => "Tarayıcıyla iletişim kurulamadı. USB / ağ kablosunu kontrol edin.",
                DeviceLocked => "Tarayıcı başka bir uygulama tarafından kilitlenmiş.",
                CoverOpen or 0x80210016 => "Tarayıcının kapağı açık.",
                LampOff or 0x80210017 => "Tarayıcı lambası kapalı.",
                MultiFeed => "Çoklu besleme algılandı (birden fazla kağıt aynı anda çekildi).",
                UserCancelled => "Tarama iptal edildi.",
                _ => string.IsNullOrWhiteSpace(ex.Message) ? "Tarayıcı bilinmeyen bir hata bildirdi." : ex.Message.Trim()
            };

            return ex is COMException || code >> 16 == 0x8021
                ? $"{text} (WIA 0x{code:X8})"
                : text;
        }
    }
}

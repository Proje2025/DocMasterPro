using System;
using System.Threading;
using DocConverter.Models;

namespace DocConverter.Services
{
    /// <summary>
    /// Tarama, kullanıcıya gösterilecek açıklamasıyla birlikte başarısız oldu.
    /// </summary>
    public class ScanFailedException : InvalidOperationException
    {
        public ScanFailedException(string message) : base(message) { }

        public ScanFailedException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Bir tarayıcıya açılmış tek bağlantı. Oluşturulduğu iş parçacığında kullanılmalı ve kapatılmalıdır.
    /// </summary>
    public interface IScanSession : IDisposable
    {
        /// <summary>Cihaza bağlanır ve tarama ayarlarını uygular.</summary>
        void Open(DeviceInfo scanner, ScanOptions options);

        /// <summary>Sıradaki sayfayı tarayıp dosyaya yazar. Besleyici boşaldığında WIA_ERROR_PAPER_EMPTY fırlatır.</summary>
        void TransferNextPage(string filePath);

        /// <summary>Cihazın gerçekten kullandığı çözünürlük.</summary>
        int EffectiveDpi { get; }

        /// <summary>Cihazın gerçekten kullandığı belge kaynağı (istenen desteklenmiyorsa farklı olabilir).</summary>
        ScanSource EffectiveSource { get; }

        /// <summary>İstenen ayardan sapıldıysa kullanıcıya gösterilecek not.</summary>
        string? Notice { get; }
    }

    public sealed record ScanAcquisitionOutcome(int PagesAcquired, bool Cancelled, string? Warning, string? Notice);

    /// <summary>
    /// Sayfa sayfa tarama döngüsü. Henüz hiç sayfa alınmamışken oluşan geçici hatalarda
    /// (cihaz uykuda, ısınıyor, meşgul, bağlantı yeni kuruluyor) bağlantıyı baştan kurup yeniden dener;
    /// böylece kullanıcının "Tara"ya ikinci kez basması gerekmez.
    /// </summary>
    public static class ScanAcquisition
    {
        /// <summary>Art arda denemeler arasındaki bekleme süreleri. Deneme sayısı = uzunluk + 1.</summary>
        public static readonly TimeSpan[] RetryDelays =
        {
            TimeSpan.FromSeconds(1.5),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5)
        };

        /// <summary>Besleyici "kağıt yok" derse (uyanırken sensör geç okunabilir) tek seferlik bekleme.</summary>
        public static readonly TimeSpan PaperEmptyRetryDelay = TimeSpan.FromSeconds(2);

        /// <summary>Sürücü hatası yüzünden sonsuz döngüye girilmesin diye tek oturumdaki sayfa üst sınırı.</summary>
        public const int MaxPagesPerSession = 1000;

        public static ScanAcquisitionOutcome Run(
            Func<IScanSession> createSession,
            DeviceInfo scanner,
            ScanOptions options,
            Func<int, string> pagePathFor,
            Action<string, IScanSession> onPage,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default,
            Action<TimeSpan>? wait = null)
        {
            ArgumentNullException.ThrowIfNull(createSession);
            ArgumentNullException.ThrowIfNull(scanner);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(pagePathFor);
            ArgumentNullException.ThrowIfNull(onPage);

            wait ??= delay => cancellationToken.WaitHandle.WaitOne(delay);

            int maxAttempts = RetryDelays.Length + 1;
            int attempt = 0;
            int pagesAcquired = 0;
            bool paperEmptyRetried = false;
            bool cancelled = false;
            string? warning = null;
            string? notice = null;
            IScanSession? session = null;

            try
            {
                while (pagesAcquired < MaxPagesPerSession)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }

                    Exception failure;
                    try
                    {
                        if (session == null)
                        {
                            attempt++;
                            session = createSession();
                            session.Open(scanner, options);
                            notice = session.Notice;
                        }

                        progress?.Report($"Sayfa {pagesAcquired + 1} taranıyor...");

                        string path = pagePathFor(pagesAcquired + 1);
                        session.TransferNextPage(path);

                        pagesAcquired++;
                        onPage(path, session);

                        if (session.EffectiveSource == ScanSource.Flatbed)
                            break;

                        continue;
                    }
                    catch (ScanFailedException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }

                    WiaFailureKind kind = WiaErrors.Classify(failure);
                    FileLogger.LogError(
                        $"WIA tarama hatası (sayfa {pagesAcquired + 1}, deneme {attempt}/{maxAttempts}, {kind})", failure);

                    if (kind == WiaFailureKind.Cancelled)
                    {
                        cancelled = true;
                        break;
                    }

                    if (pagesAcquired > 0)
                    {
                        // Besleyicinin boşalması normal bitiştir; başka her şey kullanıcıya bildirilir.
                        if (kind != WiaFailureKind.PaperEmpty)
                            warning = $"Tarama {pagesAcquired}. sayfadan sonra durdu: {WiaErrors.Describe(failure)}";
                        break;
                    }

                    if (kind == WiaFailureKind.NeedsUser)
                        throw new ScanFailedException(WiaErrors.Describe(failure), failure);

                    TimeSpan delay;
                    if (kind == WiaFailureKind.PaperEmpty)
                    {
                        if (paperEmptyRetried)
                            throw new ScanFailedException(WiaErrors.Describe(failure), failure);

                        paperEmptyRetried = true;
                        delay = PaperEmptyRetryDelay;
                    }
                    else
                    {
                        if (attempt >= maxAttempts)
                        {
                            throw new ScanFailedException(
                                $"'{scanner.Name}' {maxAttempts} denemede de yanıt vermedi.\n\n{WiaErrors.Describe(failure)}",
                                failure);
                        }

                        delay = RetryDelays[attempt - 1];
                    }

                    // İkinci tıklamanın yaptığını yap: bağlantıyı bırak, bekle, baştan bağlan.
                    session?.Dispose();
                    session = null;

                    progress?.Report($"Tarayıcı hazırlanıyor, yeniden deneniyor ({attempt + 1}/{maxAttempts})...");
                    wait(delay);
                }
            }
            finally
            {
                session?.Dispose();
            }

            return new ScanAcquisitionOutcome(pagesAcquired, cancelled, warning, notice);
        }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using DocConverter.Models;

namespace DocConverter.Services
{
    /// <summary>
    /// Microsoft Office yokken kullanılacak dönüştürme motorunu (LibreOffice) indirip hazırlar.
    /// </summary>
    public interface IOfficeEngineInstaller
    {
        /// <summary>İndirme boyutu, gereken ve mevcut disk alanı gibi kullanıcıya gösterilecek bilgiler.</summary>
        EngineSetupRequirements GetRequirements();

        /// <summary>
        /// Motoru indirir, dijital imzasını doğrular ve uygulama klasörüne hazırlar.
        /// Başarısızlıkta <see cref="EngineSetupException"/>, iptalde OperationCanceledException fırlatır.
        /// </summary>
        Task InstallAsync(IProgress<EngineSetupProgress> progress, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Arayüz geliştirme ve testler için sahte kurulum: gerçek indirme yapmaz, aşamaları taklit eder.
    /// Gerçek uygulama "Güvenli indirme" fazında yazılacak.
    /// </summary>
    public sealed class MockOfficeEngineInstaller : IOfficeEngineInstaller
    {
        private const long Megabyte = 1024L * 1024L;

        public EngineSetupRequirements Requirements { get; set; } = new(
            Version: "26.2.6",
            DownloadBytes: 373 * Megabyte,
            RequiredFreeBytes: 2048 * Megabyte,
            AvailableFreeBytes: 180L * 1024 * Megabyte,
            TargetDrive: "C:",
            Publisher: "The Document Foundation");

        /// <summary>Belirlenirse kurulum bu hatayla başarısız olur.</summary>
        public EngineSetupError? SimulatedError { get; set; }

        /// <summary>Hatanın hangi aşamada oluşacağı.</summary>
        public EngineSetupStage FailAt { get; set; } = EngineSetupStage.Downloading;

        /// <summary>Her adım arasındaki bekleme; testlerde sıfırlanır.</summary>
        public TimeSpan StepDelay { get; set; } = TimeSpan.FromMilliseconds(120);

        public int InstallCalls { get; private set; }

        public EngineSetupRequirements GetRequirements() => Requirements;

        public async Task InstallAsync(IProgress<EngineSetupProgress> progress, CancellationToken cancellationToken)
        {
            InstallCalls++;
            long total = Requirements.DownloadBytes;

            for (int step = 1; step <= 10; step++)
            {
                await Task.Delay(StepDelay, cancellationToken);
                progress.Report(new EngineSetupProgress(EngineSetupStage.Downloading, total * step / 10, total));

                if (step == 4)
                    ThrowIfFailingAt(EngineSetupStage.Downloading);
            }

            progress.Report(new EngineSetupProgress(EngineSetupStage.Verifying, 0, 0));
            await Task.Delay(StepDelay, cancellationToken);
            ThrowIfFailingAt(EngineSetupStage.Verifying);

            progress.Report(new EngineSetupProgress(EngineSetupStage.Preparing, 0, 0));
            await Task.Delay(StepDelay, cancellationToken);
            ThrowIfFailingAt(EngineSetupStage.Preparing);
        }

        private void ThrowIfFailingAt(EngineSetupStage stage)
        {
            if (SimulatedError is not { } error || FailAt != stage)
                return;

            throw new EngineSetupException(error, error switch
            {
                EngineSetupError.DownloadFailed => "Sunucuya bağlanılamadı.",
                EngineSetupError.SignatureInvalid => "Dijital imza doğrulanamadı.",
                EngineSetupError.DiskFull => "Diskte yeterli yer yok.",
                _ => "Beklenmeyen bir hata oluştu."
            });
        }
    }
}

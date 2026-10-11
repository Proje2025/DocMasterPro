# Kullanım senaryoları

Uygulamanın ana akışları ve her birini doğrulayan otomatik testler. Yeni bir özellik eklendiğinde senaryosu
buraya, testi de `DocConverter.Tests` projesine eklenir. Tümünü çalıştırmak için: `scripts\kalite.ps1`.

## Office belgelerini Microsoft Office olmadan dönüştürme

| # | Senaryo | Test |
|---|---------|------|
| 1 | Office yok → onay penceresi ne indirileceğini, boyutu, gereken alanı ve kaynağı gösterir | `OfficeEngineSetupTests.Consent_ShowsWhatWillBeDownloaded` |
| 2 | Kullanıcı onaylar → indirme, imza doğrulama, hazırlama → motor hazır, pencere kapanır | `OfficeEngineSetupTests.Start_RunsInstallerAndReportsEngineReady` |
| 3 | İndirme sırasında boyut ve yüzde gösterilir | `OfficeEngineSetupTests.DownloadProgress_IsShownAsSizeAndPercent` |
| 4 | Kullanıcı iptal eder → onay ekranına dönülür, motor hazır sayılmaz | `OfficeEngineSetupTests.Cancel_ReturnsToConsentWithoutReportingReady` |
| 5 | Pencere indirme sürerken kapatılır → indirme arka planda sürmez | `OfficeEngineSetupTests.Close_WhileWorking_CancelsTheDownload` |
| 6 | Yeterli disk alanı yok → indirme düğmesi kapalıdır | `OfficeEngineSetupTests.NotEnoughDiskSpace_BlocksTheDownloadButton` |
| 7 | İnternet yok / imza geçersiz / disk doldu → açıklayıcı hata, pencere açık kalır | `OfficeEngineSetupTests.Failure_ShowsAnExplanationAndDoesNotReportReady` |
| 8 | İmza geçersiz → dosyanın silindiği ve hiçbir şeyin çalıştırılmadığı söylenir | `OfficeEngineSetupTests.SignatureFailure_SaysNothingWasExecuted` |
| 9 | Beklenmeyen hata → ham sistem iletisi yerine genel bir açıklama gösterilir | `OfficeEngineSetupTests.UnexpectedError_ShowsAGenericMessageNotTheRawExceptionText` |
| 10 | Hatadan sonra "Yeniden dene" → kurulum baştan çalışır ve başarılı olabilir | `OfficeEngineSetupTests.Retry_AfterFailure_RunsAgainAndCanSucceed` |
| 11 | Motor göstergesi her motorun ne yapabildiğini doğru anlatır (Office / LibreOffice / yok) | `OfficeEngineSetupTests.EngineStatus_DescribesWhatEachEngineCanDo`, `EngineStatus_LibreOfficeWarnsAboutChartDifferences` |

Ekranların yerleşimi (900×600, 1280×800, 1920×1080), renk kontrastı ve klavye kullanımı
`DocMasterPro/tools/UiCheck` ile denetlenir.

## Tarama, PDF ve diğer akışlar

Bu akışların testleri `ScanAcquisitionTests`, `ScannedPdfBuilderTests`, `ScanImageCleanupTests`, `PdfServiceTests`,
`PdfStudioServiceTests`, `ConverterServiceTests`, `DeviceHubTests` ve `UpdateServiceTests` dosyalarındadır.

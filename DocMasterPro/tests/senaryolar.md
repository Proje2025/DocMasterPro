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
| 12 | Office kurulu → her zaman Microsoft Office kullanılır (LibreOffice olsa bile) | `OfficeToPdfConverterTests.OfficeInstalled_UsesMicrosoftOfficeEvenWhenLibreOfficeExists` |
| 13 | Office yok, LibreOffice var → belge LibreOffice ile dönüştürülür | `OfficeToPdfConverterTests.NoOffice_UsesLibreOffice`, `LibreOfficeRealConversionTests.*` (gerçek LibreOffice) |
| 14 | Yalnızca Word kurulu → Excel dosyaları LibreOffice'e gider | `OfficeToPdfConverterTests.OnlyWordInstalled_ExcelFilesGoToLibreOffice` |
| 15 | Hiçbir motor yok → neyin gerektiğini söyleyen açık hata | `OfficeToPdfConverterTests.NoEngine_ThrowsAClearErrorNamingWhatIsNeeded` |
| 16 | LibreOffice takılır → süre aşımında durdurulur; kullanıcı iptal ederse durdurulur | `LibreOfficeConverterTests.Timeout_StopsLibreOfficeAndSaysSo`, `Cancel_StopsLibreOffice` |
| 17 | Belge bozuk / parola korumalı → başarı yerine hata bildirilir | `LibreOfficeConverterTests.NoOutput_IsReportedAsAFailure` |
| 18 | Dönüştürme belgenin klasörüne hiçbir şey yazmaz; Türkçe ve boşluklu yollar çalışır | `LibreOfficeRealConversionTests.SourceFolder_IsLeftUntouched` |
| 19 | Eski ANSI kodlamalı metin dosyasında Türkçe karakterler korunur | `LibreOfficeRealConversionTests.PlainText_SavedInTheOldAnsiEncoding_KeepsTurkishCharacters` |
| 20 | Office ile dönüştürmeden sonra arkada gizli Word / Excel / PowerPoint kalmaz | `OfficeAutomationTests.*_ConvertsAndLeavesNoHiddenProcessBehind` (gerçek Office) |
| 21 | Motor göstergesi bilgisayarın gerçek durumunu gösterir; Word yoksa PDF → Word açıklaması görünür | `MainViewModelOfficeEngineTests.*` |

Gerçek LibreOffice ve gerçek Office gerektiren testler, ilgili program bulunamazsa atlanır. LibreOffice için sırayla
`DOCMASTER_TEST_SOFFICE` ortam değişkenindeki program klasörüne, bilgisayarda kurulu kopyaya ve
`Test_Ciktilari\lo-test\engine` klasörüne bakılır.

Ekranların yerleşimi (900×600, 1280×800, 1920×1080), renk kontrastı ve klavye kullanımı
`DocMasterPro/tools/UiCheck` ile denetlenir.

## Tarama, PDF ve diğer akışlar

Bu akışların testleri `ScanAcquisitionTests`, `ScannedPdfBuilderTests`, `ScanImageCleanupTests`, `PdfServiceTests`,
`PdfStudioServiceTests`, `ConverterServiceTests`, `DeviceHubTests` ve `UpdateServiceTests` dosyalarındadır.

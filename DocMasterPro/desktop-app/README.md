# DocMasterPro Desktop App

WPF tabanlı PDF düzenleme ve dosya dönüştürme uygulaması.

## Özellikler

- PDF birleştirme ve sayfa aralığına göre bölme
- Görüntüden PDF oluşturma
- PDF sayfalarını görüntü formatlarına dışa aktarma
- Word, Excel ve PowerPoint dosyalarını PDF'e dönüştürme
- Sayfa silme, döndürme, sıralama, çıkarma ve filigran ekleme

## Çalıştırma

```bash
cd DocMasterPro
dotnet restore DocMasterPro.sln
dotnet run --project desktop-app/DocConverter.csproj
```

## Gereksinimler

- Windows 10/11
- .NET 8 SDK veya Runtime
- Office dönüşümleri için Microsoft Office
- PDF'ten görüntü üretimi ve önizleme için Ghostscript

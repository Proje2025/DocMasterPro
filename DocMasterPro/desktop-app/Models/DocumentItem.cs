using CommunityToolkit.Mvvm.ComponentModel;

namespace DocConverter.Models
{
    /// <summary>
    /// Listedeki tek bir dosyayı temsil eder.
    /// ObservableObject'ten türetildi — Status değişiklikleri UI'da anında yansır.
    /// </summary>
    public partial class DocumentItem : ObservableObject
    {
        [ObservableProperty]
        private string fileName = string.Empty;

        [ObservableProperty]
        private string filePath = string.Empty;

        [ObservableProperty]
        private string extension = string.Empty;

        /// <summary>
        /// Mevcut işlem durumu: "Ready" | "Converting" | "Done" | "Error"
        /// </summary>
        [ObservableProperty]
        private string status = "Ready";

        /// <summary>
        /// Görüntüden dönüştürülen geçici PDF yolu (null ise dönüştürme yapılmamıştır).
        /// </summary>
        [ObservableProperty]
        private string? convertedPath;

        /// <summary>
        /// Dosya boyutu (bayt cinsinden).
        /// </summary>
        [ObservableProperty]
        private long fileSize;

        /// <summary>
        /// Formatlanmış dosya boyutu (örn: "2.5 MB").
        /// </summary>
        [ObservableProperty]
        private string fileSizeFormatted = string.Empty;

        /// <summary>
        /// PDF dosyaları için sayfa sayısı (diğer formatlar için null).
        /// </summary>
        [ObservableProperty]
        private int? pageCount;
    }
}

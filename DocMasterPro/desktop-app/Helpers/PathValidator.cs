using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DocConverter.Helpers
{
    public static class PathValidator
    {
        /// <summary>
        /// Desteklenen dosya uzantıları listesi.
        /// </summary>
        public static readonly string[] SupportedExtensions = {
            ".pdf", ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp",
            ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".txt", ".rtf", ".html", ".htm"
        };

        /// <summary>
        /// Office formatları (Word, Excel, PowerPoint).
        /// </summary>
        public static readonly string[] OfficeExtensions = {
            ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt"
        };

        /// <summary>
        /// Görüntü formatları.
        /// </summary>
        public static readonly string[] ImageExtensions = {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp"
        };

        /// <summary>
        /// Dosya yolunun güvenli olup olmadığını kontrol eder (path traversal koruması).
        /// </summary>
        public static bool IsPathSafe(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                string fullPath = Path.GetFullPath(path);
                // Path traversal kontrolü: normalleştirilmiş yol, orijinal ile tutarlı olmalı
                return !path.Contains(".." + Path.DirectorySeparatorChar)
                    && !path.Contains(".." + Path.AltDirectorySeparatorChar)
                    && Path.IsPathFullyQualified(fullPath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Dosya uzantısının desteklenip desteklenmediğini kontrol eder.
        /// </summary>
        public static bool IsSupportedExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
                return false;

            return SupportedExtensions.Contains(extension.ToLowerInvariant());
        }

        public static bool TryResolveExistingPdfPath(string path, out string fullPath)
        {
            fullPath = "";

            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                string candidate = path.Trim().Trim('"');
                fullPath = Path.GetFullPath(candidate);

                bool isExistingPdf = Path.IsPathFullyQualified(fullPath)
                    && File.Exists(fullPath)
                    && string.Equals(Path.GetExtension(fullPath), ".pdf", StringComparison.OrdinalIgnoreCase);

                if (isExistingPdf)
                    return true;

                fullPath = "";
                return false;
            }
            catch
            {
                fullPath = "";
                return false;
            }
        }

        /// <summary>
        /// "1-3, 5-7" formatındaki sayfa aralığını parse eder.
        /// </summary>
        public static List<(int From, int To)> ValidatePageRanges(string input, int maxPage)
        {
            var ranges = new List<(int From, int To)>();
            if (string.IsNullOrWhiteSpace(input))
                return ranges;

            var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var part in parts)
            {
                var dash = part.Split('-', StringSplitOptions.TrimEntries);

                if (dash.Length == 1 && int.TryParse(dash[0], out int single))
                {
                    if (single >= 1 && single <= maxPage)
                        ranges.Add((single, single));
                }
                else if (dash.Length == 2
                    && int.TryParse(dash[0], out int from)
                    && int.TryParse(dash[1], out int to))
                {
                    if (from >= 1 && to >= from && from <= maxPage)
                        ranges.Add((from, Math.Min(to, maxPage)));
                }
            }

            return ranges;
        }

        /// <summary>
        /// Bayt cinsinden boyutu okunabilir formata çevirir (örn: "2.5 MB").
        /// </summary>
        public static string FormatFileSize(long bytes)
        {
            const long KB = 1024;
            const long MB = KB * 1024;
            const long GB = MB * 1024;

            return bytes switch
            {
                >= GB => $"{(bytes / (double)GB).ToString("F2", CultureInfo.InvariantCulture)} GB",
                >= MB => $"{(bytes / (double)MB).ToString("F2", CultureInfo.InvariantCulture)} MB",
                >= KB => $"{(bytes / (double)KB).ToString("F2", CultureInfo.InvariantCulture)} KB",
                _ => $"{bytes} B"
            };
        }
    }
}

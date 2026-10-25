using System;
using System.Collections.Generic;
using System.Text;

namespace MohsenShare.Maui.Models
{
    public sealed class TransferFile
    {
        public string FileName { get; set; } = string.Empty;

        public string FilePath { get; set; } = string.Empty;

        public long FileSize { get; set; }

        public string Sha256 { get; set; } = string.Empty;
    }
}

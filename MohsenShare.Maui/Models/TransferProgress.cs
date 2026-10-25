using System;
using System.Collections.Generic;
using System.Text;

namespace MohsenShare.Maui.Models
{
    public sealed class TransferProgress
    {
        public long BytesTransferred { get; set; }

        public long TotalBytes { get; set; }

        public double Percentage =>
            TotalBytes <= 0
                ? 0
                : (double)BytesTransferred / TotalBytes * 100;

        public double SpeedBytesPerSecond { get; set; }

        public TimeSpan Elapsed { get; set; }

        public TimeSpan Remaining { get; set; }
    }
}

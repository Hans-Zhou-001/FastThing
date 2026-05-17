using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace FastThing
{
    // -----------------------------------------------------------------------
    // FileRecord – lightweight representation of an indexed file/folder
    // -----------------------------------------------------------------------
    public sealed class FileRecord
    {
        public ulong FileReferenceNumber; // MFT record number
        public ulong ParentFileReferenceNumber;
        public string Name = string.Empty;
        public long Size;
        public DateTime DateModified;
        public bool IsDirectory;
        // Resolved full path (built after all records are loaded)
        public string FullPath = string.Empty;
    }

    // -----------------------------------------------------------------------
    // NtfsIndexer – reads MFT via DeviceIoControl USN journal
    // -----------------------------------------------------------------------
    public sealed class NtfsIndexer
    {
        #region Win32 P/Invoke

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            int nInBufferSize,
            IntPtr lpOutBuffer,
            int nOutBufferSize,
            out int lpBytesReturned,
            IntPtr lpOverlapped);

        // FSCTL codes
        private const uint FSCTL_ENUM_USN_DATA = 0x900B3;
        private const uint FSCTL_QUERY_USN_JOURNAL = 0x900F4;

        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        private struct MFT_ENUM_DATA_V0
        {
            public ulong StartFileReferenceNumber;
            public long LowUsn;
            public long HighUsn;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct USN_RECORD_V2
        {
            public uint RecordLength;
            public ushort MajorVersion;
            public ushort MinorVersion;
            public ulong FileReferenceNumber;
            public ulong ParentFileReferenceNumber;
            public long Usn;
            public long TimeStamp;
            public uint Reason;
            public uint SourceInfo;
            public uint SecurityId;
            public uint FileAttributes;
            public ushort FileNameLength;
            public ushort FileNameOffset;
            // FileName follows immediately after this struct
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct USN_JOURNAL_DATA_V0
        {
            public ulong UsnJournalID;
            public long FirstUsn;
            public long NextUsn;
            public long LowestValidUsn;
            public long MaxUsn;
            public ulong MaximumSize;
            public ulong AllocationDelta;
        }

        #endregion

        public event Action<string>? StatusChanged;
        public event Action<int>? ProgressChanged; // 0-100

        /// <summary>
        /// Enumerate all files on all NTFS volumes.
        /// Returns a flat list of FileRecords with FullPath filled in.
        /// </summary>
        public List<FileRecord> BuildIndex(CancellationToken ct = default)
        {
            var result = new List<FileRecord>(4_000_000);

            var drives = DriveInfo.GetDrives();
            int total = drives.Length;
            int done = 0;

            foreach (var drive in drives)
            {
                if (ct.IsCancellationRequested) break;
                if (!drive.IsReady) { done++; continue; }
                if (drive.DriveType != DriveType.Fixed) { done++; continue; }
                if (drive.DriveFormat != "NTFS") { done++; continue; }

                StatusChanged?.Invoke($"正在索引 {drive.Name} ...");
                try
                {
                    var records = EnumerateVolume(drive.Name.TrimEnd('\\'), ct);
                    result.AddRange(records);
                }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke($"索引 {drive.Name} 失败: {ex.Message}");
                }
                done++;
                ProgressChanged?.Invoke(done * 100 / total);
            }

            StatusChanged?.Invoke("索引完成");
            return result;
        }

        private List<FileRecord> EnumerateVolume(string volume, CancellationToken ct)
        {
            // volume e.g. "C:"
            string path = @"\\.\" + volume;

            using var hVol = CreateFile(path, GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);

            if (hVol.IsInvalid)
                throw new IOException($"Cannot open volume {volume}. Try running as Administrator.");

            // Query journal to get NextUsn
            var journalData = new USN_JOURNAL_DATA_V0();
            int returned;
            var journalBuf = Marshal.AllocHGlobal(Marshal.SizeOf<USN_JOURNAL_DATA_V0>());
            try
            {
                bool ok = DeviceIoControl(hVol, FSCTL_QUERY_USN_JOURNAL,
                    IntPtr.Zero, 0,
                    journalBuf, Marshal.SizeOf<USN_JOURNAL_DATA_V0>(),
                    out returned, IntPtr.Zero);
                if (ok)
                    journalData = Marshal.PtrToStructure<USN_JOURNAL_DATA_V0>(journalBuf);
            }
            finally
            {
                Marshal.FreeHGlobal(journalBuf);
            }

            // -----------------------------------------------------------------
            // Enumerate MFT records
            // -----------------------------------------------------------------
            const int BUF_SIZE = 512 * 1024; // 512 KB read buffer
            var mftEnum = new MFT_ENUM_DATA_V0
            {
                StartFileReferenceNumber = 0,
                LowUsn = 0,
                HighUsn = journalData.NextUsn != 0 ? journalData.NextUsn : long.MaxValue
            };

            int mftSize = Marshal.SizeOf<MFT_ENUM_DATA_V0>();
            var inBuf = Marshal.AllocHGlobal(mftSize);
            var outBuf = Marshal.AllocHGlobal(BUF_SIZE);

            // Collect records – parent refs need resolving
            var map = new Dictionary<ulong, FileRecord>(500_000);
            string driveLetter = volume + "\\"; // e.g. "C:\"

            try
            {
                Marshal.StructureToPtr(mftEnum, inBuf, false);

                while (!ct.IsCancellationRequested)
                {
                    bool ok = DeviceIoControl(hVol, FSCTL_ENUM_USN_DATA,
                        inBuf, mftSize,
                        outBuf, BUF_SIZE,
                        out int bytesReturned, IntPtr.Zero);

                    if (!ok || bytesReturned <= 8) break;

                    // First 8 bytes = next StartFileReferenceNumber
                    long nextFrn = Marshal.ReadInt64(outBuf);
                    mftEnum.StartFileReferenceNumber = (ulong)nextFrn;
                    Marshal.StructureToPtr(mftEnum, inBuf, false);

                    // Walk USN records in the buffer (skip first 8 bytes)
                    IntPtr ptr = outBuf + 8;
                    IntPtr end = outBuf + bytesReturned;

                    while (ptr.ToInt64() < end.ToInt64())
                    {
                        var rec = Marshal.PtrToStructure<USN_RECORD_V2>(ptr);
                        if (rec.RecordLength == 0) break;

                        // Read file name
                        string name = Marshal.PtrToStringUni(ptr + rec.FileNameOffset, rec.FileNameLength / 2);

                        var fr = new FileRecord
                        {
                            FileReferenceNumber = rec.FileReferenceNumber & 0x0000FFFFFFFFFFFF,
                            ParentFileReferenceNumber = rec.ParentFileReferenceNumber & 0x0000FFFFFFFFFFFF,
                            Name = name,
                            IsDirectory = (rec.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0,
                            DateModified = DateTime.FromFileTimeUtc(rec.TimeStamp).ToLocalTime()
                        };

                        map[fr.FileReferenceNumber] = fr;
                        ptr += (int)rec.RecordLength;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(inBuf);
                Marshal.FreeHGlobal(outBuf);
            }

            // -----------------------------------------------------------------
            // Resolve full paths
            // -----------------------------------------------------------------
            var result = new List<FileRecord>(map.Count);
            foreach (var fr in map.Values)
            {
                if (ct.IsCancellationRequested) break;
                fr.FullPath = ResolvePath(fr, map, driveLetter);
                result.Add(fr);
            }

            return result;
        }

        private static string ResolvePath(FileRecord fr, Dictionary<ulong, FileRecord> map, string driveLetter)
        {
            // Walk up the parent chain
            var parts = new System.Collections.Generic.Stack<string>();
            parts.Push(fr.Name);

            ulong parentFrn = fr.ParentFileReferenceNumber;
            int depth = 0;
            while (depth++ < 64)
            {
                if (!map.TryGetValue(parentFrn, out var parent))
                    break;
                if (parent.FileReferenceNumber == parentFrn)
                    break; // root points to itself
                parts.Push(parent.Name);
                parentFrn = parent.ParentFileReferenceNumber;
            }

            return driveLetter + string.Join("\\", parts);
        }
    }
}

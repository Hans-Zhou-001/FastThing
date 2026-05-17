using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastThing
{
    // -----------------------------------------------------------------------
    // IndexStore – manages persistence of the file index
    // -----------------------------------------------------------------------
    public sealed class IndexStore
    {
        private const int FILE_VERSION = 2;
        private static readonly string IndexDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "FastThing");
        private static readonly string IndexPath = Path.Combine(IndexDir, "index.bin");

        // Ensure only one Save runs at a time – prevents concurrent writes to the same .tmp file
        private static readonly SemaphoreSlim _saveLock = new SemaphoreSlim(1, 1);

        public static string GetIndexPath() => IndexPath;

        // -------------------------------------------------------------------
        // Save index to binary file
        // Format: [int32 version][int64 count][records...]
        // Each record: [uint64 frn][uint64 parent][int64 size][int64 modTime][byte isDir][string path]
        // String: [int32 byteLen][utf8 bytes]
        // -------------------------------------------------------------------
        public static void Save(List<FileRecord> records)
        {
            // Block until any in-progress save finishes – prevents two concurrent writes to .tmp
            _saveLock.Wait();
            try
            {
                Directory.CreateDirectory(IndexDir);
                string tmp = IndexPath + ".tmp";

                // Remove stale tmp file if it exists (e.g. left over from a crash)
                if (File.Exists(tmp))
                {
                    try { File.Delete(tmp); } catch { /* best-effort */ }
                }

                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                               bufferSize: 1 << 20))
                using (var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false))
                {
                    bw.Write(FILE_VERSION);
                    bw.Write((long)records.Count);

                    foreach (var r in records)
                    {
                        bw.Write(r.FileReferenceNumber);
                        bw.Write(r.ParentFileReferenceNumber);
                        bw.Write(r.Size);
                        bw.Write(r.DateModified.ToBinary());
                        bw.Write(r.IsDirectory);
                        WriteString(bw, r.Name);
                        WriteString(bw, r.FullPath);
                    }

                    bw.Flush();
                    fs.Flush(flushToDisk: true);
                } // FileStream is fully closed here before we rename

                // Atomic replace: delete old → rename tmp → done
                if (File.Exists(IndexPath))
                    File.Delete(IndexPath);
                File.Move(tmp, IndexPath);
            }
            finally
            {
                _saveLock.Release();
            }
        }

        private static void WriteString(BinaryWriter bw, string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                bw.Write(0);
                return;
            }
            var bytes = Encoding.UTF8.GetBytes(s);
            bw.Write(bytes.Length);
            bw.Write(bytes);
        }

        // -------------------------------------------------------------------
        // Load index from file – returns empty list if file missing/corrupt
        // -------------------------------------------------------------------
        public static List<FileRecord> Load()
        {
            if (!File.Exists(IndexPath))
                return new List<FileRecord>();

            try
            {
                using var fs = new FileStream(IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                              bufferSize: 1 << 20);
                using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);

                int version = br.ReadInt32();
                if (version != FILE_VERSION)
                    return new List<FileRecord>(); // incompatible – rebuild

                long count = br.ReadInt64();
                var list = new List<FileRecord>((int)Math.Min(count, 8_000_000));

                for (long i = 0; i < count; i++)
                {
                    var r = new FileRecord
                    {
                        FileReferenceNumber = br.ReadUInt64(),
                        ParentFileReferenceNumber = br.ReadUInt64(),
                        Size = br.ReadInt64(),
                        DateModified = DateTime.FromBinary(br.ReadInt64()),
                        IsDirectory = br.ReadBoolean(),
                        Name = ReadString(br),
                        FullPath = ReadString(br)
                    };
                    list.Add(r);
                }

                return list;
            }
            catch
            {
                return new List<FileRecord>();
            }
        }

        private static string ReadString(BinaryReader br)
        {
            int len = br.ReadInt32();
            if (len == 0) return string.Empty;
            var bytes = br.ReadBytes(len);
            return Encoding.UTF8.GetString(bytes);
        }

        public static bool HasIndex() => File.Exists(IndexPath);

        public static DateTime GetIndexTime()
        {
            if (!File.Exists(IndexPath)) return DateTime.MinValue;
            return File.GetLastWriteTime(IndexPath);
        }
    }
}

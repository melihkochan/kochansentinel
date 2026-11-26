using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KochanSentinel.Core.Interop;
using KochanSentinel.Core.Models;

namespace KochanSentinel.Core.Services;

public sealed class QuarantineService
{
    private static readonly string QuarantineDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "KochanSentinel", "Quarantine");

    private static readonly string IndexPath = Path.Combine(QuarantineDir, "index.json");
    private static readonly byte[] LegacyXorKey = { 0x4B, 0x4F, 0x43, 0x48, 0x41, 0x4E, 0x53, 0x45 };
    private static readonly byte[] VaultMagicV2 = Encoding.ASCII.GetBytes("KOCHANAES2");
    private static readonly byte[] VaultMagicV3 = Encoding.ASCII.GetBytes("KOCHANAES3");
    private const int ChunkSize = 1024 * 1024; // 1 MB streaming chunks (prevents OOM on large files)

    public string VaultDirectory => QuarantineDir;

    public List<QuarantineEntry> LoadAll()
    {
        try
        {
            if (File.Exists(IndexPath))
            {
                var list = JsonSerializer.Deserialize<List<QuarantineEntry>>(File.ReadAllText(IndexPath));
                if (list != null) return list;
            }
        }
        catch { }
        return new List<QuarantineEntry>();
    }

    public (bool Ok, QuarantineEntry? Entry, string Message) QuarantineFile(string filePath, string sha256, string reason)
    {
        try
        {
            if (!File.Exists(filePath))
                return (false, null, "Dosya bulunamadı.");

            Directory.CreateDirectory(QuarantineDir);
            var id = Guid.NewGuid().ToString("N")[..10];
            var qFileName = $"{id}.kquarantine";
            var destPath = Path.Combine(QuarantineDir, qFileName);

            var attrs = (int)File.GetAttributes(filePath);

            // Chunked Streaming AES-256-GCM + Windows DPAPI wrapped key (supports GB-sized files cleanly)
            byte[] aesKey = RandomNumberGenerator.GetBytes(32);
            byte[] wrappedKey = NativeMethods.ProtectDpapi(aesKey);
            byte[] baseNonce = RandomNumberGenerator.GetBytes(12);

            try
            {
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var aes = new AesGcm(aesKey, 16);
                using (var src = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    dst.Write(VaultMagicV3);
                    dst.Write(BitConverter.GetBytes(wrappedKey.Length));
                    dst.Write(wrappedKey);
                    dst.Write(baseNonce);
                    dst.Write(BitConverter.GetBytes(ChunkSize));

                    byte[] plainBuf = new byte[ChunkSize];
                    byte[] cipherBuf = new byte[ChunkSize];
                    byte[] tag = new byte[16];
                    byte[] chunkNonce = new byte[12];
                    uint chunkIdx = 0;

                    while (true)
                    {
                        int read = ReadFullChunk(src, plainBuf);
                        if (read == 0 && chunkIdx > 0) break;

                        hasher.AppendData(plainBuf, 0, read);
                        DeriveChunkNonce(baseNonce, chunkIdx, chunkNonce);
                        aes.Encrypt(chunkNonce, plainBuf.AsSpan(0, read), cipherBuf.AsSpan(0, read), tag);

                        dst.Write(BitConverter.GetBytes(read));
                        dst.Write(tag);
                        if (read > 0) dst.Write(cipherBuf, 0, read);

                        chunkIdx++;
                        if (read < ChunkSize) break;
                    }
                }

                if (string.IsNullOrWhiteSpace(sha256))
                    sha256 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }
            finally
            {
                Array.Clear(aesKey);
            }

            File.SetAttributes(filePath, FileAttributes.Normal);
            File.Delete(filePath);

            var entry = new QuarantineEntry
            {
                Id = id,
                OriginalPath = filePath,
                OriginalName = Path.GetFileName(filePath),
                QuarantineFileName = qFileName,
                Sha256 = sha256,
                Reason = reason,
                OriginalAttributes = attrs
            };

            var list = LoadAll();
            list.Insert(0, entry);
            SaveIndex(list);
            return (true, entry, $"{entry.OriginalName} AES-256-GCM + DPAPI şifreli kasaya alındı.");
        }
        catch (Exception ex)
        {
            return (false, null, $"Karantinaya alınamadı: {ex.Message}");
        }
    }

    private static int ReadFullChunk(Stream stream, byte[] buffer) => ReadExact(stream, buffer, buffer.Length);

    private static int ReadExact(Stream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int r = stream.Read(buffer, total, count - total);
            if (r == 0) break;
            total += r;
        }
        return total;
    }

    private static void DeriveChunkNonce(byte[] baseNonce, uint chunkIndex, byte[] target)
    {
        Buffer.BlockCopy(baseNonce, 0, target, 0, 12);
        target[8] ^= (byte)(chunkIndex & 0xFF);
        target[9] ^= (byte)((chunkIndex >> 8) & 0xFF);
        target[10] ^= (byte)((chunkIndex >> 16) & 0xFF);
        target[11] ^= (byte)((chunkIndex >> 24) & 0xFF);
    }

    public (bool Ok, string Message) RestoreFile(QuarantineEntry entry)
    {
        try
        {
            var qPath = Path.Combine(QuarantineDir, entry.QuarantineFileName);
            if (!File.Exists(qPath))
                return (false, "Karantina dosyası bulunamadı.");

            var dir = Path.GetDirectoryName(entry.OriginalPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmpRestore = entry.OriginalPath + ".kochan_restore.tmp";

            byte[] headerMagic = new byte[10];
            int magicRead;
            using (var peek = new FileStream(qPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                magicRead = peek.Read(headerMagic, 0, headerMagic.Length);

            string restoredHash;
            if (magicRead == VaultMagicV3.Length && headerMagic.AsSpan().SequenceEqual(VaultMagicV3))
            {
                using var src = new FileStream(qPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                src.Seek(VaultMagicV3.Length, SeekOrigin.Begin);
                byte[] lenBytes = new byte[4];
                if (ReadFullChunk(src, lenBytes) != 4) return (false, "Karantina kasası başlığı bozuk!");
                int wrappedLen = BitConverter.ToInt32(lenBytes, 0);
                if (wrappedLen <= 0 || wrappedLen > 8192) return (false, "Karantina anahtar uzunluğu geçersiz!");

                byte[] wrappedKey = new byte[wrappedLen];
                byte[] baseNonce = new byte[12];
                byte[] chunkSizeBytes = new byte[4];
                if (ReadFullChunk(src, wrappedKey) != wrappedLen ||
                    ReadFullChunk(src, baseNonce) != 12 ||
                    ReadFullChunk(src, chunkSizeBytes) != 4)
                    return (false, "Karantina kasası bozuk!");

                int storedChunkSize = BitConverter.ToInt32(chunkSizeBytes, 0);
                if (storedChunkSize <= 0 || storedChunkSize > 16 * 1024 * 1024)
                    return (false, "Geçersiz blok boyutu!");

                byte[] aesKey = NativeMethods.UnprotectDpapi(wrappedKey);
                try
                {
                    using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    using var aes = new AesGcm(aesKey, 16);
                    using var dst = new FileStream(tmpRestore, FileMode.Create, FileAccess.Write, FileShare.None);

                    byte[] cipherBuf = new byte[storedChunkSize];
                    byte[] plainBuf = new byte[storedChunkSize];
                    byte[] tag = new byte[16];
                    byte[] chunkNonce = new byte[12];
                    uint chunkIdx = 0;

                    while (src.Position < src.Length)
                    {
                        if (ReadFullChunk(src, lenBytes) != 4) break;
                        int plainLen = BitConverter.ToInt32(lenBytes, 0);
                        if (plainLen < 0 || plainLen > storedChunkSize)
                            return (false, "Karantina bloğu bozuk!");
                        if (ReadFullChunk(src, tag) != 16)
                            return (false, "Karantina doğrulama etiketi eksik!");
                        if (plainLen > 0 && ReadExact(src, cipherBuf, plainLen) != plainLen)
                            return (false, "Karantina verisi eksik!");

                        DeriveChunkNonce(baseNonce, chunkIdx, chunkNonce);
                        aes.Decrypt(chunkNonce, cipherBuf.AsSpan(0, plainLen), tag, plainBuf.AsSpan(0, plainLen));
                        if (plainLen > 0)
                        {
                            hasher.AppendData(plainBuf, 0, plainLen);
                            dst.Write(plainBuf, 0, plainLen);
                        }
                        chunkIdx++;
                    }
                    restoredHash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                }
                finally
                {
                    Array.Clear(aesKey);
                }
            }
            else
            {
                byte[] raw = File.ReadAllBytes(qPath);
                byte[] plain;
                if (raw.Length >= VaultMagicV2.Length + 4 + 12 + 16 &&
                    raw.AsSpan(0, VaultMagicV2.Length).SequenceEqual(VaultMagicV2))
                {
                    int offset = VaultMagicV2.Length;
                    int wrappedLen = BitConverter.ToInt32(raw, offset);
                    offset += 4;
                    if (wrappedLen <= 0 || offset + wrappedLen + 28 > raw.Length)
                        return (false, "Karantina kasası başlığı bozuk!");

                    byte[] wrappedKey = raw.AsSpan(offset, wrappedLen).ToArray();
                    offset += wrappedLen;
                    byte[] nonce = raw.AsSpan(offset, 12).ToArray();
                    offset += 12;
                    byte[] tag = raw.AsSpan(offset, 16).ToArray();
                    offset += 16;
                    byte[] cipher = raw.AsSpan(offset).ToArray();

                    byte[] aesKey = NativeMethods.UnprotectDpapi(wrappedKey);
                    plain = new byte[cipher.Length];
                    try
                    {
                        using var aes = new AesGcm(aesKey, 16);
                        aes.Decrypt(nonce, cipher, tag, plain);
                    }
                    finally
                    {
                        Array.Clear(aesKey);
                    }
                }
                else
                {
                    // Backward compatibility for legacy XOR-protected vault entries
                    plain = raw;
                    for (int i = 0; i < plain.Length; i++)
                        plain[i] ^= LegacyXorKey[i % LegacyXorKey.Length];
                }

                restoredHash = Convert.ToHexString(SHA256.HashData(plain)).ToLowerInvariant();
                File.WriteAllBytes(tmpRestore, plain);
            }

            // Verify integrity via SHA-256 before moving back
            if (!string.IsNullOrWhiteSpace(entry.Sha256) &&
                !restoredHash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                try { if (File.Exists(tmpRestore)) File.Delete(tmpRestore); } catch { }
                return (false, "Karantina dosyası SHA-256 bütünlük doğrulamasından geçemedi!");
            }

            File.Move(tmpRestore, entry.OriginalPath, overwrite: true);

            if (entry.OriginalAttributes != 0)
            {
                try { File.SetAttributes(entry.OriginalPath, (FileAttributes)entry.OriginalAttributes); }
                catch { }
            }

            if (!File.Exists(entry.OriginalPath))
                return (false, "Geri yüklenen dosya doğrulanamadı.");

            File.Delete(qPath);

            var list = LoadAll().Where(x => x.Id != entry.Id).ToList();
            SaveIndex(list);
            return (true, $"{entry.OriginalName} AES-256/SHA-256 doğrulamasıyla orijinal konumuna geri yüklendi.");
        }
        catch (Exception ex)
        {
            return (false, $"Geri yüklenemedi: {ex.Message}");
        }
    }

    public bool DeletePermanently(QuarantineEntry entry)
    {
        try
        {
            var qPath = Path.Combine(QuarantineDir, entry.QuarantineFileName);
            if (File.Exists(qPath)) File.Delete(qPath);
            var list = LoadAll().Where(x => x.Id != entry.Id).ToList();
            SaveIndex(list);
            return true;
        }
        catch { return false; }
    }

    private static void SaveIndex(List<QuarantineEntry> list)
    {
        Directory.CreateDirectory(QuarantineDir);
        File.WriteAllText(IndexPath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }
}

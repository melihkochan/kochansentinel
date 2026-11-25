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
    private static readonly byte[] VaultMagic = Encoding.ASCII.GetBytes("KOCHANAES2");

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
            byte[] plain = File.ReadAllBytes(filePath);
            if (string.IsNullOrWhiteSpace(sha256))
                sha256 = Convert.ToHexString(SHA256.HashData(plain)).ToLowerInvariant();

            // Encrypt payload with AES-256-GCM + protect AES key using Windows DPAPI
            byte[] aesKey = RandomNumberGenerator.GetBytes(32);
            byte[] wrappedKey = NativeMethods.ProtectDpapi(aesKey);
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] cipher = new byte[plain.Length];
            byte[] tag = new byte[16];

            using (var aes = new AesGcm(aesKey, 16))
            {
                aes.Encrypt(nonce, plain, cipher, tag);
            }
            Array.Clear(aesKey);

            awaitWriteVault(destPath, wrappedKey, nonce, tag, cipher);

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

    private static void awaitWriteVault(string destPath, byte[] wrappedKey, byte[] nonce, byte[] tag, byte[] cipher)
    {
        using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(VaultMagic);
        fs.Write(BitConverter.GetBytes(wrappedKey.Length));
        fs.Write(wrappedKey);
        fs.Write(nonce);
        fs.Write(tag);
        fs.Write(cipher);
    }

    public (bool Ok, string Message) RestoreFile(QuarantineEntry entry)
    {
        try
        {
            var qPath = Path.Combine(QuarantineDir, entry.QuarantineFileName);
            if (!File.Exists(qPath))
                return (false, "Karantina dosyası bulunamadı.");

            byte[] raw = File.ReadAllBytes(qPath);
            byte[] plain;

            if (raw.Length >= VaultMagic.Length + 4 + 12 + 16 &&
                raw.AsSpan(0, VaultMagic.Length).SequenceEqual(VaultMagic))
            {
                int offset = VaultMagic.Length;
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

            // Verify integrity via SHA-256 before writing back
            if (!string.IsNullOrWhiteSpace(entry.Sha256))
            {
                string restoredHash = Convert.ToHexString(SHA256.HashData(plain)).ToLowerInvariant();
                if (!restoredHash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    return (false, "Karantina dosyası SHA-256 bütünlük doğrulamasından geçemedi!");
            }

            var dir = Path.GetDirectoryName(entry.OriginalPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Atomic write via temp staging file so target is never half-written
            var tmpRestore = entry.OriginalPath + ".kochan_restore.tmp";
            File.WriteAllBytes(tmpRestore, plain);
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

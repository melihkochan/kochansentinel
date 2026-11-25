using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KochanSentinel.Core.Interop;
using KochanSentinel.Core.Models;

namespace KochanSentinel.Core.Services;

public sealed class InstalledGame
{
    public string Name { get; set; } = "";
    public string Platform { get; set; } = ""; // Steam, Epic, Riot
    public string InstallPath { get; set; } = "";
    public string LaunchUriOrExe { get; set; } = "";
    public string IconExePath { get; set; } = "";
    public string SizeFormatted { get; set; } = "—";
    public string VerifyStatus { get; set; } = "Doğrulanmadı";
    public bool IsVerifiedOk { get; set; }
}

public sealed class GameLibraryService
{
    private static readonly HashSet<string> CommonProxyDlls = new(StringComparer.OrdinalIgnoreCase)
    {
        "version.dll", "winmm.dll", "dxgi.dll", "d3d11.dll", "d3d9.dll",
        "dbghelp.dll", "xinput1_3.dll", "dinput8.dll", "dsound.dll"
    };

    public async Task<List<InstalledGame>> DiscoverInstalledGamesAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var games = new List<InstalledGame>();
            DiscoverSteamGames(games, ct);
            DiscoverEpicGames(games, ct);
            DiscoverRiotGames(games);

            return games
                .GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(g => g.Name)
                .ToList();
        }, ct);
    }

    private static void DiscoverSteamGames(List<InstalledGame> games, CancellationToken ct)
    {
        try
        {
            var steamPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
                @"D:\Steam", @"D:\SteamLibrary", @"E:\Steam", @"E:\SteamLibrary"
            };

            var libraryFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sp in steamPaths.Where(Directory.Exists))
            {
                var sa = Path.Combine(sp, "steamapps");
                if (Directory.Exists(sa)) libraryFolders.Add(sa);

                var vdf = Path.Combine(sa, "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    var text = File.ReadAllText(vdf);
                    foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
                    {
                        var lib = m.Groups[1].Value.Replace(@"\\", @"\");
                        var libSa = Path.Combine(lib, "steamapps");
                        if (Directory.Exists(libSa)) libraryFolders.Add(libSa);
                    }
                }
            }

            foreach (var lib in libraryFolders)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var acf in Directory.EnumerateFiles(lib, "appmanifest_*.acf"))
                {
                    try
                    {
                        var content = File.ReadAllText(acf);
                        var appId = Regex.Match(content, "\"appid\"\\s+\"(\\d+)\"").Groups[1].Value;
                        var name = Regex.Match(content, "\"name\"\\s+\"([^\"]+)\"").Groups[1].Value;
                        var installDir = Regex.Match(content, "\"installdir\"\\s+\"([^\"]+)\"").Groups[1].Value;
                        var sizeStr = Regex.Match(content, "\"SizeOnDisk\"\\s+\"(\\d+)\"").Groups[1].Value;

                        if (string.IsNullOrWhiteSpace(name) || name.Contains("Steamworks Common", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var fullPath = Path.Combine(lib, "common", installDir);
                        if (!Directory.Exists(fullPath)) continue;

                        long.TryParse(sizeStr, out long sizeBytes);
                        games.Add(new InstalledGame
                        {
                            Name = name,
                            Platform = "Steam",
                            InstallPath = fullPath,
                            LaunchUriOrExe = $"steam://rungameid/{appId}",
                            IconExePath = FindPrimaryExe(fullPath),
                            SizeFormatted = sizeBytes > 0 ? JunkCategory.FormatBytes(sizeBytes) : "Kurulu"
                        });
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private static void DiscoverEpicGames(List<InstalledGame> games, CancellationToken ct)
    {
        try
        {
            var manifestDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Epic\EpicGamesLauncher\Data\Manifests");
            if (!Directory.Exists(manifestDir)) return;

            foreach (var itemFile in Directory.EnumerateFiles(manifestDir, "*.item"))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(itemFile));
                    var root = doc.RootElement;
                    var name = root.TryGetProperty("DisplayName", out var dn) ? dn.GetString() : null;
                    var loc = root.TryGetProperty("InstallLocation", out var il) ? il.GetString() : null;
                    var launchExe = root.TryGetProperty("LaunchExecutable", out var le) ? le.GetString() : null;
                    long sizeBytes = root.TryGetProperty("InstallSize", out var isz) && isz.TryGetInt64(out var sz) ? sz : 0;

                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(loc) || !Directory.Exists(loc))
                        continue;

                    var fullExe = !string.IsNullOrWhiteSpace(launchExe) ? Path.Combine(loc, launchExe) : loc;
                    games.Add(new InstalledGame
                    {
                        Name = name,
                        Platform = "Epic Games",
                        InstallPath = loc,
                        LaunchUriOrExe = File.Exists(fullExe) ? fullExe : loc,
                        IconExePath = File.Exists(fullExe) ? fullExe : FindPrimaryExe(loc),
                        SizeFormatted = sizeBytes > 0 ? JunkCategory.FormatBytes(sizeBytes) : "Kurulu"
                    });
                }
                catch { }
            }
        }
        catch { }
    }

    private static void DiscoverRiotGames(List<InstalledGame> games)
    {
        try
        {
            var riotJson = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Riot Games\RiotClientInstalls.json");
            if (!File.Exists(riotJson)) return;

            var text = File.ReadAllText(riotJson);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("associated_client", out var clients))
            {
                foreach (var prop in clients.EnumerateObject())
                {
                    var path = prop.Name;
                    if (!Directory.Exists(path)) continue;
                    string gameName = path.Contains("VALORANT", StringComparison.OrdinalIgnoreCase) ? "VALORANT" :
                                      path.Contains("League of Legends", StringComparison.OrdinalIgnoreCase) ? "League of Legends" :
                                      Path.GetFileName(path.TrimEnd('\\', '/'));

                    games.Add(new InstalledGame
                    {
                        Name = gameName,
                        Platform = "Riot Games",
                        InstallPath = path,
                        LaunchUriOrExe = path,
                        IconExePath = FindPrimaryExe(path),
                        SizeFormatted = "Kurulu"
                    });
                }
            }
        }
        catch { }
    }

    private static string FindPrimaryExe(string dir)
    {
        try
        {
            var topExe = Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => !Path.GetFileName(f).Contains("unins", StringComparison.OrdinalIgnoreCase) &&
                                     !Path.GetFileName(f).Contains("crash", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(topExe)) return topExe;

            var opts = new System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 2 };
            return Directory.EnumerateFiles(dir, "*.exe", opts)
                .FirstOrDefault(f => !Path.GetFileName(f).Contains("unins", StringComparison.OrdinalIgnoreCase) &&
                                     !Path.GetFileName(f).Contains("crash", StringComparison.OrdinalIgnoreCase)) ?? "";
        }
        catch
        {
            return "";
        }
    }

    public async Task<(bool Ok, string Summary)> VerifyGameFolderAsync(InstalledGame game, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            if (!Directory.Exists(game.InstallPath))
                return (false, "Hata: Oyun klasörü bulunamadı!");

            int exeCount = 0;
            int unsignedDllWarnings = 0;
            var warnings = new List<string>();

            try
            {
                var opts = new System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 3 };
                foreach (var file in Directory.EnumerateFiles(game.InstallPath, "*", opts))
                {
                    ct.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(file);
                    var ext = Path.GetExtension(file);

                    if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                        exeCount++;

                    // Check for suspicious unsigned proxy DLLs (common DLL hijack / cheat injector leftover)
                    if (CommonProxyDlls.Contains(name))
                    {
                        bool signed = NativeMethods.VerifyFileSignature(file);
                        if (!signed)
                        {
                            unsignedDllWarnings++;
                            warnings.Add(name);
                        }
                    }
                }
            }
            catch { }

            if (exeCount == 0)
                return (false, "Uyarı: Klasörde çalıştırılabilir oyun (.exe) bulunamadı (eksik/bozuk kurulum olabilir).");

            if (unsignedDllWarnings > 0)
                return (false, $"Dikkat: {unsignedDllWarnings} adet imzasız enjeksiyon/proxy DLL bulundu ({string.Join(", ", warnings.Take(3))})!");

            return (true, $"Doğrulandı: {exeCount} çalıştırılabilir dosya temiz, şüpheli DLL enjeksiyonu yok.");
        }, ct);
    }
}

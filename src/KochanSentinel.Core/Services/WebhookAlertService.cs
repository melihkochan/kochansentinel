using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using KochanSentinel.Core.Models;

namespace KochanSentinel.Core.Services;

/// <summary>
/// Optional asynchronous Discord Webhook alarm dispatcher for High/Critical security events.
/// </summary>
public sealed class WebhookAlertService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private DateTime _lastSentUtc = DateTime.MinValue;

    public async Task<(bool Ok, string Message)> SendAlertAsync(string webhookUrl, string title, string description, FindingSeverity severity, bool bypassRateLimit = false)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
            return (false, "Webhook URL tanımlı değil.");

        var trimmed = webhookUrl.Trim();
        if (!trimmed.StartsWith("https://discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://discordapp.com/api/webhooks/", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Geçerli bir Discord Webhook URL'si (https://discord.com/api/webhooks/...) girin.");
        }

        if (!bypassRateLimit && (DateTime.UtcNow - _lastSentUtc).TotalSeconds < 6)
            return (false, "Rate limit nedeniyle atlandı.");

        _lastSentUtc = DateTime.UtcNow;

        try
        {
            int color = severity switch
            {
                FindingSeverity.Critical => 0xEF4444,
                FindingSeverity.High => 0xF97316,
                FindingSeverity.Medium => 0xF59E0B,
                _ => 0x22C55E
            };

            var payload = new
            {
                username = "KOCHAN Sentinel Guard",
                embeds = new[]
                {
                    new
                    {
                        title = $"🛡️ {title}",
                        description,
                        color,
                        fields = new[]
                        {
                            new { name = "Bilgisayar", value = Environment.MachineName, inline = true },
                            new { name = "Önem", value = severity.ToString(), inline = true },
                            new { name = "Zaman", value = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"), inline = true }
                        },
                        footer = new { text = $"KOCHAN Sentinel v{UpdateService.GetCurrentAppVersion()}" }
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(trimmed, content);

            return resp.IsSuccessStatusCode
                ? (true, "Discord Webhook bildirimi başarıyla gönderildi!")
                : (false, $"Discord Webhook hatası (HTTP {(int)resp.StatusCode}).");
        }
        catch (Exception ex)
        {
            return (false, $"Webhook gönderilemedi: {ex.Message}");
        }
    }
}

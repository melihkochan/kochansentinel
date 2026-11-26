using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using KochanSentinel.Core.Models;

namespace KochanSentinel.Core.Services;

/// <summary>
/// Exports a sleek, standalone, dark-mode HTML System Health & Security Diagnostic Report
/// (zero external CDN dependencies; printable to PDF from any browser).
/// </summary>
public sealed class ReportExportService
{
    public string ExportHtmlReport(
        string outputPath,
        SystemMetrics metrics,
        BenchmarkResult? latestBenchmark,
        BootPerformanceInfo? bootInfo,
        IEnumerable<SecurityFinding> findings,
        IEnumerable<FirewallRuleItem> firewallRules,
        IEnumerable<StartupAppItem> startupApps,
        IEnumerable<SecurityHistoryEntry> history)
    {
        var sb = new StringBuilder(16384);
        string E(string? s) => WebUtility.HtmlEncode(s ?? "");

        var findingList = findings.ToList();
        var fwList = firewallRules.ToList();
        var startupList = startupApps.ToList();
        var histList = history.Take(35).ToList();

        string healthColor = metrics.HealthScore >= 85 ? "#22c55e" : metrics.HealthScore >= 65 ? "#f59e0b" : "#ef4444";

        sb.Append($$"""
<!DOCTYPE html>
<html lang="tr">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>KOCHAN Sentinel — Sistem Sağlık ve Güvenlik Raporu</title>
<style>
  :root { --bg:#09090b; --card:#111113; --card2:#18181b; --bd:#27272a; --txt:#fafafa; --muted:#a1a1aa; }
  * { box-sizing:border-box; }
  body { margin:0; padding:28px; background:var(--bg); color:var(--txt); font-family:"Segoe UI",system-ui,sans-serif; line-height:1.5; }
  .wrap { max-width:1080px; margin:0 auto; }
  .hero { display:flex; justify-content:space-between; align-items:center; background:var(--card); border:1px solid var(--bd); border-radius:12px; padding:22px 26px; margin-bottom:18px; }
  .hero h1 { margin:0; font-size:22px; }
  .hero p { margin:4px 0 0; color:var(--muted); font-size:13px; }
  .score { text-align:center; background:var(--card2); border:2px solid {{healthColor}}; border-radius:12px; padding:10px 22px; }
  .score .num { font-size:30px; font-weight:800; color:{{healthColor}}; }
  .score .lbl { font-size:11px; color:var(--muted); }
  .grid4 { display:grid; grid-template-columns:repeat(4,1fr); gap:12px; margin-bottom:18px; }
  .card { background:var(--card); border:1px solid var(--bd); border-radius:10px; padding:16px; margin-bottom:16px; }
  .kpi { background:var(--card); border:1px solid var(--bd); border-radius:10px; padding:14px 16px; }
  .kpi .k { font-size:11px; text-transform:uppercase; color:var(--muted); font-weight:600; }
  .kpi .v { font-size:20px; font-weight:700; margin-top:4px; }
  .kpi .s { font-size:11.5px; color:var(--muted); margin-top:2px; }
  h2 { margin:0 0 12px; font-size:15.5px; border-bottom:1px solid var(--bd); padding-bottom:8px; }
  table { width:100%; border-collapse:collapse; font-size:12.5px; }
  th, td { text-align:left; padding:8px 10px; border-bottom:1px solid var(--bd); vertical-align:top; }
  th { color:var(--muted); font-weight:600; background:var(--card2); }
  .badge { display:inline-block; padding:2px 8px; border-radius:999px; font-size:11px; font-weight:700; }
  .b-crit { background:#3f1518; color:#fca5a5; border:1px solid #7f1d1d; }
  .b-high { background:#431407; color:#fdba74; border:1px solid #9a3412; }
  .b-med  { background:#422006; color:#fde047; border:1px solid #854d0e; }
  .b-ok   { background:#052e16; color:#86efac; border:1px solid #166534; }
  .mono { font-family:Consolas,monospace; font-size:11.5px; color:#d4d4d8; word-break:break-all; }
  @media print { body { background:#fff; color:#111; } .card,.hero,.kpi { border-color:#ccc; background:#fff; } }
</style>
</head>
<body>
<div class="wrap">
  <div class="hero">
    <div>
      <h1>KOCHAN Sentinel — Sistem Sağlık &amp; Güvenlik Raporu</h1>
      <p>Makine: <strong>{{E(Environment.MachineName)}}</strong> &nbsp;|&nbsp; Sürüm: <strong>v{{E(UpdateService.GetCurrentAppVersion())}}</strong> &nbsp;|&nbsp; Tarih: {{DateTime.Now:dd.MM.yyyy HH:mm:ss}}</p>
      <p>CPU: {{E(metrics.CpuName)}} &nbsp;|&nbsp; GPU: {{E(metrics.GpuName)}}</p>
    </div>
    <div class="score">
      <div class="num">{{metrics.HealthScore}}/100</div>
      <div class="lbl">{{E(metrics.HealthStatusText)}}</div>
    </div>
  </div>

  <div class="grid4">
    <div class="kpi"><div class="k">CPU &amp; Sıcaklık</div><div class="v">{{metrics.CpuUsagePercent:F1}}%</div><div class="s">{{(metrics.CpuTempCelsius.HasValue ? $"{metrics.CpuTempCelsius:F0}°C" : "Sıcaklık --")}} • {{metrics.CpuLogicalProcessors}} Thread</div></div>
    <div class="kpi"><div class="k">RAM Bellek</div><div class="v">{{metrics.RamUsagePercent:F0}}%</div><div class="s">{{metrics.RamUsedGb:F1}} / {{metrics.RamTotalGb:F1}} GB</div></div>
    <div class="kpi"><div class="k">GPU &amp; Ağ</div><div class="v">{{(metrics.GpuTempCelsius.HasValue ? $"{metrics.GpuTempCelsius:F0}°C" : "--")}}</div><div class="s">Ping: {{(metrics.PingMs.HasValue ? $"{metrics.PingMs} ms" : "--")}} • Jitter: {{metrics.PingJitterMs:F1}} ms</div></div>
    <div class="kpi"><div class="k">Güç &amp; Açılış</div><div class="v">{{(bootInfo?.LastBootSeconds.HasValue == true ? $"{bootInfo.LastBootSeconds:F1} sn" : metrics.ActivePowerPlan)}}</div><div class="s">Plan: {{E(metrics.ActivePowerPlan)}} • Sensör: {{E(metrics.SensorDriverStatus)}}</div></div>
  </div>
""");

        // Drives & SMART
        sb.Append("<div class='card'><h2>Disk Sürücüleri &amp; S.M.A.R.T. Sağlığı</h2><table><thead><tr><th>Sürücü</th><th>Etiket</th><th>Boş / Toplam</th><th>Doluluk</th></tr></thead><tbody>");
        foreach (var d in metrics.Drives)
            sb.Append($"<tr><td><strong>{E(d.Name)}</strong></td><td>{E(d.Label)}</td><td>{d.FreeGb:F1} GB / {d.TotalGb:F0} GB</td><td>%{d.UsedPercent:F1}</td></tr>");
        sb.Append("</tbody></table>");
        if (metrics.SmartDrives.Count > 0)
        {
            sb.Append("<p style='margin:10px 0 0;color:#a1a1aa;font-size:12px;'><strong>S.M.A.R.T.:</strong> " +
                      string.Join(" &nbsp;|&nbsp; ", metrics.SmartDrives.Select(s => $"{E(s.Name)}: {E(s.SummaryText)}")) + "</p>");
        }
        sb.Append("</div>");

        // Latest Benchmark
        if (latestBenchmark != null)
        {
            sb.Append($$"""
<div class="card">
  <h2>Son Donanım Benchmark &amp; Termal Analiz ({{E(latestBenchmark.TimestampText)}})</h2>
  <p style="margin:0 0 8px;color:#d4d4d8;font-size:13px;">
    <strong>Genel: %{{latestBenchmark.OverallScore}}</strong> ({{E(latestBenchmark.TierBadge)}}) &nbsp;|&nbsp;
    Oyun: %{{latestBenchmark.GamingPercent}} &nbsp;|&nbsp; Masaüstü: %{{latestBenchmark.DesktopPercent}} &nbsp;|&nbsp; İş İstasyonu: %{{latestBenchmark.WorkstationPercent}}
  </p>
  <p style="margin:0 0 6px;color:#a1a1aa;font-size:12.5px;">
    CPU 1C/MC: {{latestBenchmark.CpuSingleScore}} / {{latestBenchmark.CpuMultiScore}} &nbsp;|&nbsp;
    RAM: {{latestBenchmark.RamBandwidthGbPerSec:F1}} GB/s ({{latestBenchmark.RamLatencyNs:F1}} ns) &nbsp;|&nbsp;
    Disk ({{E(latestBenchmark.TargetDrive)}}): {{latestBenchmark.DiskSeqReadMbps:F0}} / {{latestBenchmark.DiskSeqWriteMbps:F0}} MB/s ({{latestBenchmark.DiskRandom4kReadIops:N0}} 4K IOPS)
  </p>
  <p style="margin:0;color:{{(latestBenchmark.ThermalThrottlingDetected ? "#fca5a5" : "#86efac")}};font-size:12.5px;">
    <strong>Termal Durum:</strong> {{E(latestBenchmark.ThermalSummary)}} — {{E(latestBenchmark.BottleneckNote)}}
  </p>
</div>
""");
        }

        // Security Findings
        sb.Append($"<div class='card'><h2>Güvenlik &amp; Kalıntı Bulguları ({findingList.Count})</h2>");
        if (findingList.Count == 0)
        {
            sb.Append("<p><span class='badge b-ok'>Temiz</span> Aktif yüksek riskli bulgu veya kalıntı tespit edilmedi.</p>");
        }
        else
        {
            sb.Append("<table><thead><tr><th>Risk</th><th>Kategori</th><th>Öğe</th><th>Açıklama</th><th>Hedef</th></tr></thead><tbody>");
            foreach (var f in findingList)
            {
                string cls = f.Severity == FindingSeverity.Critical ? "b-crit" : f.Severity == FindingSeverity.High ? "b-high" : f.Severity == FindingSeverity.Medium ? "b-med" : "b-ok";
                sb.Append($"<tr><td><span class='badge {cls}'>{E(f.Severity.ToString())}</span></td><td>{E(f.Category)}</td><td><strong>{E(f.Name)}</strong></td><td>{E(f.Reason)}</td><td class='mono'>{E(f.TargetPath)}</td></tr>");
            }
            sb.Append("</tbody></table>");
        }
        sb.Append("</div>");

        // Startup & Firewall summary
        if (startupList.Count > 0)
        {
            sb.Append($"<div class='card'><h2>Başlangıç Uygulamaları ({startupList.Count})</h2><table><thead><tr><th>Ad</th><th>Kaynak</th><th>Durum</th><th>Etki</th><th>İmza</th></tr></thead><tbody>");
            foreach (var s in startupList)
                sb.Append($"<tr><td><strong>{E(s.Name)}</strong></td><td>{E(s.Source)}</td><td>{E(s.StatusText)}</td><td>{E(s.ImpactLevel)}</td><td>{(s.IsSigned ? "İmzalı" : "İmzasız")}</td></tr>");
            sb.Append("</tbody></table></div>");
        }

        if (fwList.Count > 0)
        {
            sb.Append($"<div class='card'><h2>KOCHAN Firewall Engelleri ({fwList.Count})</h2><table><thead><tr><th>Kural</th><th>Uygulama</th><th>Konum</th></tr></thead><tbody>");
            foreach (var r in fwList)
                sb.Append($"<tr><td>{E(r.RuleName)}</td><td><strong>{E(r.ProcessName)}</strong></td><td class='mono'>{E(r.ProgramPath)}</td></tr>");
            sb.Append("</tbody></table></div>");
        }

        if (histList.Count > 0)
        {
            sb.Append($"<div class='card'><h2>Son Güvenlik Olay Geçmişi ({histList.Count})</h2><table><thead><tr><th>Tarih</th><th>Tür</th><th>Hedef</th><th>Sonuç</th></tr></thead><tbody>");
            foreach (var h in histList)
                sb.Append($"<tr><td>{E(h.DateTimeText)}</td><td>{E(h.ActionType)}</td><td>{E(h.TargetName)}</td><td>{E(h.Verdict)}</td></tr>");
            sb.Append("</tbody></table></div>");
        }

        sb.Append("</div></body></html>");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
        return outputPath;
    }
}

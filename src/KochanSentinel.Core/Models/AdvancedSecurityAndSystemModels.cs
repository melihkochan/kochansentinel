using System;
using System.Collections.Generic;

namespace KochanSentinel.Core.Models;

public sealed class KernelDriverItem
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public string State { get; set; } = "";
    public bool IsSigned { get; set; }
    public bool IsOutsideSystem32 { get; set; }
    public bool IsKnownVulnerableByovd { get; set; }
    public FindingSeverity Severity { get; set; } = FindingSeverity.Info;
    public string Reason { get; set; } = "";
}

public sealed class NetworkConnectionEntry
{
    public int Pid { get; set; }
    public string ProcessName { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string RemoteIp { get; set; } = "";
    public int RemotePort { get; set; }
    public string CountryOrOrg { get; set; } = "Sorgulanıyor...";
    public bool IsSuspicious { get; set; }
    public FindingSeverity Severity { get; set; } = FindingSeverity.Info;
    public string Reason { get; set; } = "";
    public string EndpointText => $"{RemoteIp}:{RemotePort}";
}

public sealed class FirewallRuleItem
{
    public string RuleName { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public string ProgramPath { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class DnsCacheEntry
{
    public string Domain { get; set; } = "";
    public string RecordData { get; set; } = "";
    public string RecordType { get; set; } = "";
    public bool IsSuspicious { get; set; }
}

public sealed class LoadedDllModuleInfo
{
    public int Pid { get; set; }
    public string ProcessName { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public long SizeKb { get; set; }
    public bool IsSigned { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class BrowserExtensionIssue
{
    public string Browser { get; set; } = "";
    public string Profile { get; set; } = "";
    public string Name { get; set; } = "";
    public string ExtensionId { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public string RiskReason { get; set; } = "";
    public FindingSeverity Severity { get; set; } = FindingSeverity.Medium;
}

public sealed class SmartDriveHealth
{
    public string Name { get; set; } = "";
    public double? TemperatureC { get; set; }
    public double? RemainingLifePercent { get; set; }
    public double? TotalWrittenGb { get; set; }
    public bool PredictFailure { get; set; }
    public string SummaryText { get; set; } = "Sağlıklı";
}

public sealed class SystemTweakItem
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsApplied { get; set; }
    public string StatusBadge => IsApplied ? "AKTİF (Optimize)" : "Varsayılan";
    public string ActionText => IsApplied ? "Varsayılana Döndür" : "Uygula";
}

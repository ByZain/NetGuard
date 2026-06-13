using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetClean;

internal static class UploadLimiter
{
    public static UploadLimitResult ApplyLimit(string appPath, int kilobytesPerSecond)
    {
        if (string.IsNullOrWhiteSpace(appPath) || !File.Exists(appPath))
        {
            return UploadLimitResult.Fail("无法限速：选中的程序路径不可用。");
        }

        if (kilobytesPerSecond <= 0)
        {
            return UploadLimitResult.Fail("限速值必须大于 0。");
        }

        var bitsPerSecond = (ulong)kilobytesPerSecond * 1024UL * 8UL;
        var policyName = GetPolicyName(appPath, kilobytesPerSecond);
        var policyPrefix = GetPolicyPrefix(appPath);
        var legacyPolicyName = GetLegacyPolicyName(appPath);
        var command = string.Join(Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            "Import-Module NetQos",
            "$name = " + PowerShellString(policyName),
            "$prefix = " + PowerShellString(policyPrefix),
            "$legacy = " + PowerShellString(legacyPolicyName),
            "$path = " + PowerShellString(appPath),
            "$existing = Get-NetQosPolicy -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Where-Object { $_.Name -like ($prefix + '*') -or $_.Name -eq $legacy }",
            "foreach ($item in @($existing)) {",
            "    Remove-NetQosPolicy -Name $item.Name -PolicyStore ActiveStore -Confirm:$false",
            "}",
            $"New-NetQosPolicy -Name $name -AppPathNameMatchCondition $path -ThrottleRateActionBitsPerSecond {bitsPerSecond} -PolicyStore ActiveStore | Out-Null");

        var result = RunPowerShell(command);
        if (!result.Succeeded)
        {
            return UploadLimitResult.Fail($"限速失败：{result.Output}");
        }

        return UploadLimitResult.Success($"已限制上传：{kilobytesPerSecond} KB/s。\n策略名：{policyName}\n注意：该策略保存在 Windows ActiveStore，重启后会失效，也可以点“取消限速”移除。");
    }

    public static UploadLimitResult ClearLimit(string appPath)
    {
        if (string.IsNullOrWhiteSpace(appPath))
        {
            return UploadLimitResult.Fail("无法取消限速：选中的程序路径不可用。");
        }

        var policyPrefix = GetPolicyPrefix(appPath);
        var legacyPolicyName = GetLegacyPolicyName(appPath);
        var command = string.Join(Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            "Import-Module NetQos",
            "$prefix = " + PowerShellString(policyPrefix),
            "$legacy = " + PowerShellString(legacyPolicyName),
            "$existing = Get-NetQosPolicy -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Where-Object { $_.Name -like ($prefix + '*') -or $_.Name -eq $legacy }",
            "if (@($existing).Count -gt 0) {",
            "    foreach ($item in @($existing)) { Remove-NetQosPolicy -Name $item.Name -PolicyStore ActiveStore -Confirm:$false }",
            "    'removed'",
            "} else {",
            "    'missing'",
            "}");

        var result = RunPowerShell(command);
        if (!result.Succeeded)
        {
            return UploadLimitResult.Fail($"取消限速失败：{result.Output}");
        }

        return result.Output.Contains("removed", StringComparison.OrdinalIgnoreCase)
            ? UploadLimitResult.Success("已取消上传限速。")
            : UploadLimitResult.Success("没有找到该程序的 NetGuard 限速策略。");
    }

    public static IReadOnlyDictionary<string, UploadLimitInfo> GetActiveLimits()
    {
        var command = string.Join(Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            "Import-Module NetQos",
            "$items = Get-NetQosPolicy -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'NetGuard_*' -or $_.Name -like 'NetClean_*' }",
            "$result = foreach ($p in @($items)) {",
            "    $path = $null",
            "    foreach ($prop in @('AppPathNameMatchCondition', 'AppPathName')) {",
            "        if ($p.PSObject.Properties[$prop] -and $p.$prop) { $path = [string]$p.$prop; break }",
            "    }",
            "    $rate = $null",
            "    foreach ($prop in @('ThrottleRateActionBitsPerSecond', 'ThrottleRate')) {",
            "        if ($p.PSObject.Properties[$prop] -and $p.$prop) { $rate = [string]$p.$prop; break }",
            "    }",
            "    [pscustomobject]@{ Name = [string]$p.Name; Path = [string]$path; Rate = [string]$rate }",
            "}",
            "@($result) | ConvertTo-Json -Compress");

        var result = RunPowerShell(command);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.Output) || result.Output == "无输出")
        {
            return new Dictionary<string, UploadLimitInfo>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var policies = JsonSerializer.Deserialize<List<QosPolicyDto>>(result.Output) ?? [];
            var limits = new Dictionary<string, UploadLimitInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var policy in policies)
            {
                if (string.IsNullOrWhiteSpace(policy.Path))
                {
                    continue;
                }

                limits[policy.Path] = new UploadLimitInfo(
                    policy.Name ?? "",
                    policy.Path,
                    TryReadKilobytesPerSecond(policy.Name, policy.Rate));
            }

            return limits;
        }
        catch
        {
            return new Dictionary<string, UploadLimitInfo>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string GetPolicyName(string appPath, int kilobytesPerSecond)
    {
        return $"{GetPolicyPrefix(appPath)}_{kilobytesPerSecond}KBps";
    }

    private static string GetPolicyPrefix(string appPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(appPath.ToLowerInvariant()));
        return "NetGuard_" + Convert.ToHexString(hash, 0, 8);
    }

    private static string GetLegacyPolicyName(string appPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(appPath.ToLowerInvariant()));
        return "NetClean_" + Convert.ToHexString(hash, 0, 8);
    }

    private static int? TryReadKilobytesPerSecond(string? policyName, string? rate)
    {
        if (!string.IsNullOrWhiteSpace(policyName))
        {
            var nameMatch = Regex.Match(policyName, @"_(\d+)KBps$", RegexOptions.IgnoreCase);
            if (nameMatch.Success && int.TryParse(nameMatch.Groups[1].Value, out var kbpsFromName))
            {
                return kbpsFromName;
            }
        }

        if (string.IsNullOrWhiteSpace(rate))
        {
            return null;
        }

        if (ulong.TryParse(rate, out var bitsPerSecond))
        {
            return (int)Math.Max(1, bitsPerSecond / 8UL / 1024UL);
        }

        var match = Regex.Match(rate, @"([\d.]+)\s*([KMG]?)(?:Bits|Bit|bps|/s)", RegexOptions.IgnoreCase);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, out var value))
        {
            return null;
        }

        var multiplier = match.Groups[2].Value.ToUpperInvariant() switch
        {
            "G" => 1024d * 1024d * 1024d,
            "M" => 1024d * 1024d,
            "K" => 1024d,
            _ => 1d
        };
        var parsedBits = value * multiplier;
        return (int)Math.Max(1, Math.Round(parsedBits / 8d / 1024d));
    }

    private static string PowerShellString(string value)
    {
        return "'" + value.Replace("'", "''") + "'";
    }

    private static PowerShellResult RunPowerShell(string command)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var powershellPath = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershellPath))
        {
            powershellPath = "powershell.exe";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = powershellPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encoded);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return new PowerShellResult(false, "无法启动 PowerShell。");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            return new PowerShellResult(false, "PowerShell 执行超时。");
        }

        var output = (outputTask.GetAwaiter().GetResult() + "\n" + errorTask.GetAwaiter().GetResult()).Trim();
        return new PowerShellResult(process.ExitCode == 0, string.IsNullOrWhiteSpace(output) ? "无输出" : output);
    }
}

internal sealed record UploadLimitResult(bool Succeeded, string Message)
{
    public static UploadLimitResult Success(string message) => new(true, message);
    public static UploadLimitResult Fail(string message) => new(false, message);
}

internal sealed record PowerShellResult(bool Succeeded, string Output);

internal sealed record UploadLimitInfo(
    string PolicyName,
    string Path,
    int? KilobytesPerSecond)
{
    public string DisplayText => KilobytesPerSecond.HasValue ? $"{KilobytesPerSecond.Value} KB/s" : "已限速";
}

internal sealed class QosPolicyDto
{
    public string? Name { get; set; }
    public string? Path { get; set; }
    public string? Rate { get; set; }
}

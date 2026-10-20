# USBPcap 抓包助手：先扫描所有根集线器，找出哪一条上有 G3 A 的流量。
# 用法（管理员）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\capture-usb.ps1 -Scan
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\capture-usb.ps1 -Hub 1 -Seconds 40 -Out "$env:TEMP\g3a.pcap"
param(
    [switch]$Scan,
    [int]$Hub = 1,
    [int]$Seconds = 40,
    [string]$Out = "$env:TEMP\g3a.pcap"
)

$ErrorActionPreference = 'Stop'
$cmd = "C:\Program Files\USBPcap\USBPcapCMD.exe"
if (-not (Test-Path $cmd)) { throw "未安装 USBPcap：$cmd" }

# USBPcapCMD 的参数里含反斜杠设备名，必须用数组逐个传，不能被 PowerShell 拼成一个字符串
function Invoke-Capture {
    param([string]$Device, [string]$File, [int]$Sec)

    if (Test-Path $File) { Remove-Item $File -Force }
    $cmdArgs = @('-d', $Device, '-A', '-o', $File, '-s', '65535')
    $p = Start-Process -FilePath $cmd -ArgumentList $cmdArgs -NoNewWindow -PassThru `
                       -RedirectStandardOutput "$File.stdout.txt" -RedirectStandardError "$File.stderr.txt"
    Write-Host "  启动 PID=$($p.Id) → $Device"
    for ($i = 0; $i -lt $Sec; $i++) {
        Start-Sleep -Seconds 1
        $p.Refresh()
        if ($p.HasExited) { break }
    }
    $alive = -not $p.HasExited
    if ($alive) { $p.Kill() }
    $size = 0
    if (Test-Path $File) { $size = (Get-Item $File).Length }
    $err = Get-Content "$File.stderr.txt" -Raw -ErrorAction SilentlyContinue
    Write-Host "  存活 $alive，抓包文件 $size B"
    if ($err) { Write-Host "  stderr: $($err.Trim())" }
    return $size
}

if ($Scan) {
    "扫描 \\.\USBPcap1..8（每个 5 秒，仅用于定位设备所在的根集线器）"
    foreach ($i in 1..8) {
        "USBPcap${i}:"
        try {
            Invoke-Capture -Device "\\.\USBPcap$i" -File "$env:TEMP\scan$i.pcap" -Sec 5 | Out-Null
        } catch {
            "  无法打开：$($_.Exception.Message)"
        }
    }
    "扫描完成。请选择抓包文件最大（>0）的那个编号作为 -Hub。"
    exit 0
}

"开始抓包：\\.\USBPcap$Hub，${Seconds} 秒 → $Out"
"现在请打开网页版 M HUB WEB 并连接 G3 A，让它在界面上读出电量。"
Invoke-Capture -Device "\\.\USBPcap$Hub" -File $Out -Sec $Seconds | Out-Null
if (Test-Path $Out) { "完成：$Out（{0:N1} KB）" -f ((Get-Item $Out).Length / 1KB) } else { "未生成抓包文件" }

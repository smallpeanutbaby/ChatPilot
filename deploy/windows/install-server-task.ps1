<#
.SYNOPSIS
  ChatPilot 服务器：Windows 登录后自动启动（计划任务，以当前用户身份运行）。

.DESCRIPTION
  适合把服务器和节点放在同一台 Windows 电脑上的用法：服务器只听本机（appsettings.json 里 ListenAddress 设成 127.0.0.1、关闭 TLS），
  微信消息由服务器主动去腾讯取，不需要公网 IP。要接入其他电脑时，请部署到云服务器（见 deploy/linux）。
  先运行一次 chatpilot-server.exe login 扫码绑定微信，再以普通权限运行本脚本：
    powershell -ExecutionPolicy Bypass -File install-server-task.ps1 -ServerPath C:\Tools\chatpilot\chatpilot-server.exe
  卸载：Unregister-ScheduledTask -TaskName ChatPilotServer -Confirm:$false
  日志：%LOCALAPPDATA%\ChatPilot\server.log
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $ServerPath
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $ServerPath)) {
    throw "找不到 $ServerPath"
}

$logDir = Join-Path $env:LOCALAPPDATA 'ChatPilot'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir 'server.log'

# 通过 cmd 把输出追加到日志文件；窗口隐藏。工作目录设成程序所在目录，这样能读到旁边的 appsettings.json。
$action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/c `"`"$ServerPath`" run >> `"$log`" 2>&1`"" -WorkingDirectory (Split-Path $ServerPath -Parent)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) `
    -StartWhenAvailable
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName 'ChatPilotServer' -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName 'ChatPilotServer'
Write-Host "✅ 已注册并启动计划任务 ChatPilotServer，日志：$log"

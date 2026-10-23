<#
.SYNOPSIS
  ChatPilot 节点：Windows 登录后自动启动（计划任务，以当前用户身份运行）。

.DESCRIPTION
  不要注册成 Windows 服务：服务跑在系统账户下，访问不到你用户下的 herdr 和 Claude。
  先运行一次 chatpilot-node.exe pair ... 完成配对，再以普通权限运行本脚本：
    powershell -ExecutionPolicy Bypass -File install-node-task.ps1 -NodePath C:\Tools\chatpilot\chatpilot-node.exe
  卸载：Unregister-ScheduledTask -TaskName ChatPilotNode -Confirm:$false
  日志：%LOCALAPPDATA%\ChatPilot\node.log
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $NodePath
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $NodePath)) {
    throw "找不到 $NodePath"
}

$logDir = Join-Path $env:LOCALAPPDATA 'ChatPilot'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir 'node.log'

# 通过 cmd 把输出追加到日志文件；窗口隐藏。
$action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/c `"`"$NodePath`" run >> `"$log`" 2>&1`""
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) `
    -StartWhenAvailable
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName 'ChatPilotNode' -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName 'ChatPilotNode'
Write-Host "✅ 已注册并启动计划任务 ChatPilotNode，日志：$log"

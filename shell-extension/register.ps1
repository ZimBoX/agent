#Requires -Version 5.1
<#
    Регистрирует пункт контекстного меню "Отправить в Claude Assistant" для текущего
    пользователя. Прав администратора не требуется — используется HKCU\Software\Classes,
    который Windows объединяет с HKEY_CLASSES_ROOT для процессов текущего пользователя
    (включая explorer.exe, который всегда работает от имени вошедшего пользователя).

    В реальном инсталляторе (Этап 11) этот же приём выполняется автоматически при установке.
#>

$ErrorActionPreference = "Stop"

$ClsId = "{B932951B-8D25-4B01-9CD6-C0AB6801A39E}"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ComHostPath = Join-Path $ScriptDir "ClaudeAssistant.ShellExtension\bin\x64\Debug\net8.0-windows\ClaudeAssistant.ShellExtension.comhost.dll"

# Намеренно указывает на установленную через Velopack копию (%LocalAppData%\...\current\),
# а не на dev-сборку (bin\...\Debug\...) — так и должно быть в реальной поставке
# (Этап 11: инсталлятор пропишет ровно этот путь), и это же спасает от повторения
# уже случавшегося бага: `obj`/`bin` клиента периодически чистятся при пересборках
# (см. Этап 11 чеклиста, баг с .pri), из-за чего путь к Debug-сборке переставал
# существовать и пункт меню в Проводнике переставал вызывать клиент — молча, потому
# что SendToClaudeCommand.Invoke() перехватывает все исключения, чтобы не уронить
# explorer.exe. "current" же всегда актуален, пока установлена хоть одна версия.
$ClientExePath = Join-Path $env:LOCALAPPDATA "ClaudeAssistant.Client\current\ClaudeAssistant.Client.exe"
if (-not (Test-Path $ClientExePath)) {
    throw "Не найден установленный клиент по пути $ClientExePath — сначала поставь ClaudeAssistant.Client через Setup.exe"
}

if (-not (Test-Path $ComHostPath)) {
    throw "Не найден comhost.dll по пути $ComHostPath — сначала собери shell-extension проект"
}

Write-Host "comhost.dll: $ComHostPath"
Write-Host "client exe:  $ClientExePath"

# 1. Путь к клиенту — читает SendToClaudeCommand.GetClientExePath()
New-Item -Path "HKCU:\Software\ClaudeAssistant" -Force | Out-Null
Set-ItemProperty -Path "HKCU:\Software\ClaudeAssistant" -Name "ClientExePath" -Value $ClientExePath

# 2. Регистрация CLSID нашего COM-класса
$ClsidKey = "HKCU:\Software\Classes\CLSID\$ClsId"
New-Item -Path $ClsidKey -Force | Out-Null
Set-ItemProperty -Path $ClsidKey -Name "(Default)" -Value "ClaudeAssistant SendTo Command"

$InprocKey = "$ClsidKey\InprocServer32"
New-Item -Path $InprocKey -Force | Out-Null
Set-ItemProperty -Path $InprocKey -Name "(Default)" -Value $ComHostPath
Set-ItemProperty -Path $InprocKey -Name "ThreadingModel" -Value "Both"

# 3. Пункт контекстного меню для любого файла, привязанный к нашей команде через
#    ExplorerCommandHandler — Explorer сам находит CLSID и вызывает IExplorerCommand.
$ShellKey = "HKCU:\Software\Classes\*\shell\ClaudeAssistant"
New-Item -Path $ShellKey -Force | Out-Null
Set-ItemProperty -Path $ShellKey -Name "(Default)" -Value "Отправить в Claude Assistant"
Set-ItemProperty -Path $ShellKey -Name "ExplorerCommandHandler" -Value $ClsId

# 4. То же самое для папок
$DirShellKey = "HKCU:\Software\Classes\Directory\shell\ClaudeAssistant"
New-Item -Path $DirShellKey -Force | Out-Null
Set-ItemProperty -Path $DirShellKey -Name "(Default)" -Value "Отправить в Claude Assistant"
Set-ItemProperty -Path $DirShellKey -Name "ExplorerCommandHandler" -Value $ClsId

Write-Host "Готово. Пункт меню зарегистрирован (может понадобиться перезапуск explorer.exe)."

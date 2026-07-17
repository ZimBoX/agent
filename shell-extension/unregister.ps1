#Requires -Version 5.1
<#
    Откатывает register.ps1 — удаляет пункт контекстного меню и регистрацию COM-класса.
#>

$ClsId = "{B932951B-8D25-4B01-9CD6-C0AB6801A39E}"

Remove-Item -Path "HKCU:\Software\Classes\*\shell\ClaudeAssistant" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "HKCU:\Software\Classes\Directory\shell\ClaudeAssistant" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "HKCU:\Software\Classes\CLSID\$ClsId" -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Готово. Регистрация удалена."

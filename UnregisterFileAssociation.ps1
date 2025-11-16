# Unregister MicaPDF as PDF viewer
# Run this script as Administrator

$progId = "MicaPDF.Document"
$appName = "MicaPDF"

# Check if running as administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Please run this script as Administrator!" -ForegroundColor Red
    Write-Host "Right-click on the script and select 'Run as Administrator'" -ForegroundColor Yellow
    pause
    exit 1
}

Write-Host "Unregistering MicaPDF..." -ForegroundColor Yellow

# Remove ProgID
if (Test-Path "HKCU:\Software\Classes\$progId") {
    Remove-Item -Path "HKCU:\Software\Classes\$progId" -Recurse -Force
    Write-Host "Removed ProgID: $progId" -ForegroundColor Green
}

# Remove .pdf association if it points to MicaPDF
$pdfDefault = Get-ItemProperty -Path "HKCU:\Software\Classes\.pdf" -Name "(Default)" -ErrorAction SilentlyContinue
if ($pdfDefault.'(Default)' -eq $progId) {
    Remove-ItemProperty -Path "HKCU:\Software\Classes\.pdf" -Name "(Default)" -Force -ErrorAction SilentlyContinue
    Write-Host "Removed .pdf default association" -ForegroundColor Green
}

# Remove from OpenWithProgids
if (Test-Path "HKCU:\Software\Classes\.pdf\OpenWithProgids") {
    Remove-ItemProperty -Path "HKCU:\Software\Classes\.pdf\OpenWithProgids" -Name $progId -Force -ErrorAction SilentlyContinue
    Write-Host "Removed from OpenWithProgids" -ForegroundColor Green
}

# Remove capabilities
if (Test-Path "HKCU:\Software\$appName") {
    Remove-Item -Path "HKCU:\Software\$appName" -Recurse -Force
    Write-Host "Removed capabilities" -ForegroundColor Green
}

# Remove from RegisteredApplications
Remove-ItemProperty -Path "HKCU:\Software\RegisteredApplications" -Name $appName -Force -ErrorAction SilentlyContinue
Write-Host "Removed from RegisteredApplications" -ForegroundColor Green

Write-Host ""
Write-Host "MicaPDF has been unregistered successfully!" -ForegroundColor Green
Write-Host ""
pause

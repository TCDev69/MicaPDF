# Register MicaPDF as default PDF viewer
# Run this script as Administrator

$exePath = "$PSScriptRoot\Release\PDFViewer.exe"
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

# Check if exe exists
if (-not (Test-Path $exePath)) {
    Write-Host "Error: PDFViewer.exe not found in Release folder!" -ForegroundColor Red
    Write-Host "Please build the release version first using:" -ForegroundColor Yellow
    Write-Host "  dotnet publish -c Release -r win-x64 -p:Platform=x64 --self-contained true -p:PublishSingleFile=true" -ForegroundColor Yellow
    pause
    exit 1
}

Write-Host "Registering MicaPDF as PDF viewer..." -ForegroundColor Green

# Register ProgID
New-Item -Path "HKCU:\Software\Classes\$progId" -Force | Out-Null
Set-ItemProperty -Path "HKCU:\Software\Classes\$progId" -Name "(Default)" -Value "PDF Document"
Set-ItemProperty -Path "HKCU:\Software\Classes\$progId" -Name "FriendlyTypeName" -Value "$appName PDF Document"

# Register application
New-Item -Path "HKCU:\Software\Classes\$progId\Application" -Force | Out-Null
Set-ItemProperty -Path "HKCU:\Software\Classes\$progId\Application" -Name "ApplicationName" -Value $appName
Set-ItemProperty -Path "HKCU:\Software\Classes\$progId\Application" -Name "ApplicationDescription" -Value "Modern PDF viewer with Mica design"

# Register default icon
New-Item -Path "HKCU:\Software\Classes\$progId\DefaultIcon" -Force | Out-Null
Set-ItemProperty -Path "HKCU:\Software\Classes\$progId\DefaultIcon" -Name "(Default)" -Value "$exePath,0"

# Register open command
New-Item -Path "HKCU:\Software\Classes\$progId\shell\open\command" -Force | Out-Null
Set-ItemProperty -Path "HKCU:\Software\Classes\$progId\shell\open\command" -Name "(Default)" -Value "`"$exePath`" `"%1`""

# Associate .pdf extension
New-Item -Path "HKCU:\Software\Classes\.pdf" -Force | Out-Null
Set-ItemProperty -Path "HKCU:\Software\Classes\.pdf" -Name "(Default)" -Value $progId

# Add to OpenWithProgids
New-Item -Path "HKCU:\Software\Classes\.pdf\OpenWithProgids" -Force | Out-Null
Set-ItemProperty -Path "HKCU:\Software\Classes\.pdf\OpenWithProgids" -Name $progId -Value ([byte[]]@()) -Type Binary

# Register capabilities
New-Item -Path "HKCU:\Software\$appName\Capabilities" -Force | Out-Null
Set-ItemProperty -Path "HKCU:\Software\$appName\Capabilities" -Name "ApplicationName" -Value $appName
Set-ItemProperty -Path "HKCU:\Software\$appName\Capabilities" -Name "ApplicationDescription" -Value "Modern PDF viewer with translucent Mica background"

New-Item -Path "HKCU:\Software\$appName\Capabilities\FileAssociations" -Force | Out-Null
Set-ItemProperty -Path "HKCU:\Software\$appName\Capabilities\FileAssociations" -Name ".pdf" -Value $progId

# Register application
New-Item -Path "HKCU:\Software\RegisteredApplications" -Force | Out-Null
Set-ItemProperty -Path "HKCU:\Software\RegisteredApplications" -Name $appName -Value "Software\$appName\Capabilities"

Write-Host ""
Write-Host "Registration completed successfully!" -ForegroundColor Green
Write-Host ""
Write-Host "MicaPDF has been registered as a PDF viewer." -ForegroundColor Cyan
Write-Host "To set it as default:" -ForegroundColor Yellow
Write-Host "1. Right-click any PDF file" -ForegroundColor White
Write-Host "2. Select 'Open with' > 'Choose another app'" -ForegroundColor White
Write-Host "3. Check 'Always use this app to open .pdf files'" -ForegroundColor White
Write-Host "4. Select MicaPDF from the list" -ForegroundColor White
Write-Host ""
Write-Host "Or go to Settings > Apps > Default apps > MicaPDF" -ForegroundColor White
Write-Host ""
pause

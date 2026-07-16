<#
.SYNOPSIS
    Stores the live-test tenant credentials in .NET user secrets.

.DESCRIPTION
    Reads TenantId / ClientId / certificate reference from the local GraphMailer
    runtime config (C:\ProgramData\GraphMailer\config\graphmailer.json) and writes
    them into the user-secrets store of GraphMailer.Tests.Live
    (%APPDATA%\Microsoft\UserSecrets\GraphMailer.Tests.Live\secrets.json — outside
    the repository, never committed).

    Sender/recipient addresses are not part of the runtime config and must be
    passed as parameters (or set manually with `dotnet user-secrets set`).

.EXAMPLE
    .\tools\set-live-test-secrets.ps1 -SenderAddress relay@test.example.com -RecipientAddress inbox@test.example.com

.EXAMPLE
    .\tools\set-live-test-secrets.ps1 -SenderAddress relay@t.com -RecipientAddress inbox@t.com -SenderAlias alias@t.com
#>
param(
    [Parameter(Mandatory)] [string] $SenderAddress,
    [Parameter(Mandatory)] [string] $RecipientAddress,
    # A SECONDARY smtp proxyAddress of the SenderAddress mailbox itself
    # (Get-Mailbox <sender> | Select -Expand EmailAddresses) — NOT the address
    # of a different user; the alias test sends as the resolved mailbox owner.
    [string] $SenderAlias,
    [string] $ConfigPath = 'C:\ProgramData\GraphMailer\config\graphmailer.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$project = Join-Path (Split-Path $PSScriptRoot -Parent) 'tests\GraphMailer.Tests.Live'

if (-not (Test-Path $ConfigPath)) { throw "Runtime config not found: $ConfigPath" }
$graphApi = (Get-Content $ConfigPath -Raw | ConvertFrom-Json).GraphApi

if ([string]::IsNullOrWhiteSpace($graphApi.TenantId) -or [string]::IsNullOrWhiteSpace($graphApi.ClientId)) {
    throw "GraphApi.TenantId/ClientId missing in $ConfigPath — run the Entra setup wizard first."
}

function Set-Secret([string]$key, [string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { return }
    dotnet user-secrets set $key $value --project $project | Out-Null
    Write-Host "  set $key"
}

Write-Host "Writing user secrets for GraphMailer.Tests.Live ..."
Set-Secret 'LiveTests:TenantId'               $graphApi.TenantId
Set-Secret 'LiveTests:ClientId'               $graphApi.ClientId
Set-Secret 'LiveTests:CertificateThumbprint'  $graphApi.ClientCertificateThumbprint
Set-Secret 'LiveTests:CertificateSubjectName' $graphApi.ClientCertificateSubjectName
# Note: a ClientSecret in the runtime config is stored encrypted (ENC[...]) and
# cannot be reused here. Certificate auth needs no secret at all; otherwise run:
#   dotnet user-secrets set "LiveTests:ClientSecret" "<secret>" --project tests\GraphMailer.Tests.Live
Set-Secret 'LiveTests:SenderAddress'          $SenderAddress
Set-Secret 'LiveTests:RecipientAddress'       $RecipientAddress
Set-Secret 'LiveTests:SenderAlias'            $SenderAlias

Write-Host ''
Write-Host 'Done. Run the live tests with:'
Write-Host '  dotnet test tests\GraphMailer.Tests.Live'

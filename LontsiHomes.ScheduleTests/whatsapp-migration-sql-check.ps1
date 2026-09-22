# Executes the migration's raw SQL guard in an isolated, disposable LocalDB database.
# Never points at the application database or production.
$ErrorActionPreference = 'Stop'
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../LontsiHomes.API/Migrations/20260911020456_AddInfobipMessagingAndWhatsAppConsent.cs') -Raw
$guard = [regex]::Match($source, 'migrationBuilder\.Sql\(@"(?<sql>[\s\S]*?)"\);').Groups['sql'].Value
if ([string]::IsNullOrWhiteSpace($guard)) { throw 'Migration SQL guard not found.' }

$database = 'LontsiHomesMigrationRegression_' + [guid]::NewGuid().ToString('N')
$connection = New-Object System.Data.SqlClient.SqlConnection('Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=true;Connect Timeout=30')
function Invoke-TestSql([string] $sql) {
    $command = $connection.CreateCommand()
    try {
        $command.CommandText = $sql
        $command.CommandTimeout = 30
        [void] $command.ExecuteNonQuery()
    } finally { $command.Dispose() }
}
$created = $false
try {
    $connection.Open()
    Invoke-TestSql "CREATE DATABASE [$database]"
    $created = $true
    $connection.ChangeDatabase($database)
    Invoke-TestSql 'CREATE TABLE dbo.AspNetUsers (WhatsAppPhoneNumber nvarchar(max) NULL); INSERT dbo.AspNetUsers VALUES (N''+237600000001'');'
    foreach ($case in @('default', 'no-default', 'already-removed')) {
        Invoke-TestSql 'CREATE TABLE dbo.PlatformPaymentSettings (Id int NOT NULL);'
        if ($case -eq 'default') {
            # Includes a closing bracket to exercise QUOTENAME escaping.
            Invoke-TestSql 'ALTER TABLE dbo.PlatformPaymentSettings ADD SkipLandlordPhoneVerification bit NOT NULL CONSTRAINT [DF_Test]]Quoted] DEFAULT 0;'
        } elseif ($case -eq 'no-default') {
            Invoke-TestSql 'ALTER TABLE dbo.PlatformPaymentSettings ADD SkipLandlordPhoneVerification bit NULL;'
        }
        Invoke-TestSql $guard
        Invoke-TestSql "IF COL_LENGTH(N'dbo.PlatformPaymentSettings', N'SkipLandlordPhoneVerification') IS NOT NULL THROW 51010, 'Obsolete column still exists', 1;"
        Invoke-TestSql $guard
        Invoke-TestSql 'DROP TABLE dbo.PlatformPaymentSettings;'
        Write-Output "PASS: $case and repeat execution"
    }
    Invoke-TestSql 'CREATE TABLE dbo.PlatformPaymentSettings (Id int, SkipLandlordPhoneVerification bit); INSERT dbo.AspNetUsers VALUES (N''12345678901234567'');'
    $blocked = $false
    try { Invoke-TestSql $guard } catch {
        if ($_.Exception.ToString() -notmatch 'legacy phone numbers exceed 16 characters') { throw }
        $blocked = $true
    }
    if (-not $blocked) { throw 'Overlength phone guard did not block.' }
    Invoke-TestSql "IF COL_LENGTH(N'dbo.PlatformPaymentSettings', N'SkipLandlordPhoneVerification') IS NULL THROW 51011, 'Guard changed schema before blocking', 1; IF NOT EXISTS (SELECT 1 FROM dbo.AspNetUsers WHERE WhatsAppPhoneNumber = N'12345678901234567') THROW 51012, 'Phone data changed', 1;"
    Write-Output 'PASS: overlength phone blocks without changing data or schema'
} finally {
    if ($created) {
        $connection.ChangeDatabase('master')
        Invoke-TestSql "ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$database];"
        Write-Output "Removed disposable test database: $database"
    }
    $connection.Dispose()
}

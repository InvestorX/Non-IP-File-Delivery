# 自己署名証明書生成スクリプト (PowerShell版)
# Non-IP File Delivery Web Configuration用

Write-Host "======================================" -ForegroundColor Cyan
Write-Host "自己署名証明書生成スクリプト" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

# 証明書ディレクトリの作成
$certDir = "certificates"
if (-not (Test-Path $certDir)) {
    New-Item -ItemType Directory -Path $certDir | Out-Null
}

# 証明書のパラメータ
$certName = "localhost"
$certPath = Join-Path $certDir "$certName.pfx"
$dnsNames = @("localhost", "*.localhost")
$ipAddresses = @("127.0.0.1", "::1")
$notAfter = (Get-Date).AddYears(10)  # 10年間有効
$password = ""

Write-Host "証明書パラメータ:" -ForegroundColor Yellow
Write-Host "  証明書名: $certName"
Write-Host "  DNS名: $($dnsNames -join ', ')"
Write-Host "  IPアドレス: $($ipAddresses -join ', ')"
Write-Host "  有効期限: $notAfter"
Write-Host "  出力先: $certPath"
Write-Host ""

# 自己署名証明書を生成
Write-Host "証明書を生成中..." -ForegroundColor Green

try {
    # 証明書の生成
    $cert = New-SelfSignedCertificate `
        -Subject "CN=localhost, O=NonIP File Delivery, OU=Development" `
        -DnsName $dnsNames `
        -KeyAlgorithm RSA `
        -KeyLength 4096 `
        -NotAfter $notAfter `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -FriendlyName "NonIP File Delivery Development Certificate" `
        -HashAlgorithm SHA256 `
        -KeyUsage DigitalSignature, KeyEncipherment, DataEncipherment `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.1,1.3.6.1.5.5.7.3.2")

    Write-Host "証明書を生成しました: $($cert.Thumbprint)" -ForegroundColor Green

    # PFX形式でエクスポート
    Write-Host ""
    Write-Host "PFX形式でエクスポート中..." -ForegroundColor Green

    if ($password -eq "") {
        $securePassword = New-Object System.Security.SecureString
    } else {
        $securePassword = ConvertTo-SecureString -String $password -Force -AsPlainText
    }

    Export-PfxCertificate `
        -Cert $cert `
        -FilePath $certPath `
        -Password $securePassword | Out-Null

    Write-Host "PFX証明書を生成しました: $certPath" -ForegroundColor Green

    # 証明書情報の表示
    Write-Host ""
    Write-Host "======================================" -ForegroundColor Cyan
    Write-Host "証明書情報:" -ForegroundColor Cyan
    Write-Host "======================================" -ForegroundColor Cyan
    Write-Host "  サブジェクト: $($cert.Subject)"
    Write-Host "  発行者: $($cert.Issuer)"
    Write-Host "  サムプリント: $($cert.Thumbprint)"
    Write-Host "  有効期間: $($cert.NotBefore) - $($cert.NotAfter)"
    Write-Host "  DNS名: $($cert.DnsNameList.Unicode -join ', ')"
    Write-Host ""

    Write-Host "======================================" -ForegroundColor Cyan
    Write-Host "生成完了" -ForegroundColor Cyan
    Write-Host "======================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "以下のファイルが生成されました:" -ForegroundColor Yellow
    Write-Host "  - $certPath (ASP.NET Core用)"
    Write-Host ""
    Write-Host "証明書は以下の場所にもインストールされました:" -ForegroundColor Yellow
    Write-Host "  - Cert:\CurrentUser\My\$($cert.Thumbprint)"
    Write-Host ""
    Write-Host "使用方法:" -ForegroundColor Yellow
    Write-Host "  1. appsettings.jsonで証明書パスを設定してください"
    Write-Host "  2. ブラウザで https://localhost:5001 にアクセス"
    Write-Host "  3. 証明書警告が表示されたら「詳細設定」→「続行」"
    Write-Host ""
    Write-Host "信頼された証明書ストアにインポートする場合:" -ForegroundColor Yellow
    Write-Host "  Import-Certificate -FilePath '$certPath' -CertStoreLocation Cert:\CurrentUser\Root" -ForegroundColor Gray
    Write-Host ""
    Write-Host "注意:" -ForegroundColor Red
    Write-Host "  この証明書は開発/テスト環境専用です"
    Write-Host "  本番環境では正式なCA署名証明書を使用してください"
    Write-Host ""
    Write-Host "完了しました！" -ForegroundColor Green

} catch {
    Write-Host ""
    Write-Host "エラーが発生しました: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "スタックトレース: $($_.ScriptStackTrace)" -ForegroundColor Red
    exit 1
}

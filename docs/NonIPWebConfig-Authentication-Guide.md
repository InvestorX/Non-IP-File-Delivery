# NonIPWebConfig 認証機能ガイド

## 概要

NonIPWebConfigには、閉域環境向けのセキュアな認証機能が実装されています。この機能は外部の認証基盤に依存せず、ローカルで完結する設計となっています。

## セキュリティ機能

### 🔐 認証方式
- **JWT（JSON Web Token）ベース認証**
- **BCryptによるパスワードハッシュ化**
- **トークン有効期限管理**（デフォルト8時間）
- **ログイン失敗回数制限**（デフォルト5回でアカウントロック）

### 🔒 HTTPS対応
- **自己署名証明書によるHTTPS通信**
- **開発環境向けに最適化**
- **10年間有効な証明書**

## 初期セットアップ

### 1. 証明書の生成

#### Linux/macOS:
```bash
cd src/NonIPWebConfig
chmod +x generate-certificate.sh
./generate-certificate.sh
```

#### Windows (PowerShell):
```powershell
cd src/NonIPWebConfig
.\generate-certificate.ps1
```

### 2. アプリケーションの起動

```bash
cd src/NonIPWebConfig
dotnet run
```

起動すると、以下の情報が表示されます：

```
🌐 Non-IP Web Configuration Tool が起動しました
📱 HTTP: http://localhost:5000
📱 HTTPS: https://localhost:5001

⚠️  初回ログイン情報:
   ユーザー名: admin
   パスワード: Admin@123
   (初回ログイン後、必ずパスワードを変更してください)
```

### 3. 初回ログイン

1. ブラウザで `https://localhost:5001` にアクセス
2. 証明書の警告が表示されたら「詳細設定」→「続行」をクリック
3. ログインページで初期認証情報を入力:
   - ユーザー名: `admin`
   - パスワード: `Admin@123`
4. ログイン成功後、パスワード変更画面に自動遷移
5. 新しいパスワードを設定（8文字以上、大文字・小文字・数字・特殊文字を含む）

## パスワードポリシー

新しいパスワードは以下の要件を満たす必要があります：

- **最小文字数**: 8文字以上
- **大文字**: 少なくとも1文字
- **小文字**: 少なくとも1文字
- **数字**: 少なくとも1文字
- **特殊文字**: 少なくとも1文字 (`@$!%*?&`)

**例**: `MyP@ssw0rd`, `SecureP@ss2024`

## API認証

### ログインAPI

**エンドポイント**: `POST /api/auth/login`

**リクエスト**:
```json
{
  "username": "admin",
  "password": "your-password"
}
```

**レスポンス（成功時）**:
```json
{
  "success": true,
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "username": "admin",
  "role": "Admin",
  "mustChangePassword": false,
  "expiresAt": "2025-10-09T06:00:00Z",
  "message": "ログインに成功しました"
}
```

### 認証が必要なAPIの使用

すべての設定API（`/api/config`, `/api/status`）は認証が必要です。

**リクエストヘッダーにトークンを含める**:
```http
GET /api/config HTTP/1.1
Host: localhost:5001
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
```

**JavaScript例**:
```javascript
const token = localStorage.getItem('authToken');

fetch('/api/config', {
    headers: {
        'Authorization': `Bearer ${token}`
    }
})
.then(response => response.json())
.then(data => console.log(data));
```

### パスワード変更API

**エンドポイント**: `POST /api/auth/change-password`

**リクエスト**:
```json
{
  "currentPassword": "old-password",
  "newPassword": "NewP@ssw0rd123",
  "confirmPassword": "NewP@ssw0rd123"
}
```

### ログアウトAPI

**エンドポイント**: `POST /api/auth/logout`

**リクエスト**: 認証トークンのみ必要（ヘッダーに含める）

**レスポンス**:
```json
{
  "success": true,
  "message": "ログアウトしました"
}
```

## セキュリティ設定

### appsettings.json

```json
{
  "Auth": {
    "JwtSecret": "your-secret-key-here",
    "JwtExpirationHours": "8",
    "MaxFailedAttempts": "5",
    "UserStoragePath": "users.json"
  },
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://localhost:5000"
      },
      "Https": {
        "Url": "https://localhost:5001",
        "Certificate": {
          "Path": "certificates/localhost.pfx",
          "Password": ""
        }
      }
    }
  }
}
```

### JWT Secret の生成

初回起動時に自動生成されますが、手動で設定する場合：

```bash
# Linux/macOS
openssl rand -base64 64

# PowerShell
[Convert]::ToBase64String((1..64 | ForEach-Object { Get-Random -Maximum 256 }))
```

生成された文字列を `appsettings.json` の `Auth:JwtSecret` に設定してください。

## トラブルシューティング

### 証明書の警告が表示される

**原因**: 自己署名証明書のため、ブラウザが信頼していません。

**対処法**:
1. **開発環境**: 警告を無視して「詳細設定」→「続行」をクリック
2. **本番環境**: 証明書をシステムの信頼ストアに追加

#### Linux:
```bash
sudo cp certificates/localhost.crt /usr/local/share/ca-certificates/
sudo update-ca-certificates
```

#### macOS:
```bash
sudo security add-trusted-cert -d -r trustRoot \
  -k /Library/Keychains/System.keychain certificates/localhost.crt
```

#### Windows:
1. `certificates/localhost.crt` をダブルクリック
2. 「証明書のインストール」をクリック
3. 「現在のユーザー」または「ローカルマシン」を選択
4. 「証明書をすべて次のストアに配置する」を選択
5. 「信頼されたルート証明機関」を選択

### アカウントがロックされた

**原因**: ログイン失敗が5回以上発生しました。

**対処法**:
1. `users.json` ファイルを削除（すべてのユーザーがリセットされます）
2. アプリケーションを再起動
3. 初期管理者アカウントが再作成されます

### トークンの有効期限切れ

**現象**: APIリクエストが401 Unauthorizedエラーを返す

**対処法**: 再度ログインしてください。トークンは8時間で自動的に期限切れになります。

## ベストプラクティス

### 🔒 セキュリティ

1. **初期パスワードは必ず変更する**
2. **強力なパスワードを使用する**（12文字以上推奨）
3. **JWTシークレットをランダムに生成し、定期的に変更する**
4. **本番環境では認証局が発行した証明書を使用する**
5. **ログを定期的に確認し、不正アクセスを監視する**

### 📝 運用

1. **定期的なバックアップ**
   - `users.json` ファイルをバックアップ
   - `appsettings.json` をバージョン管理（秘密情報は除く）

2. **ログ監視**
   - 認証失敗のログを監視
   - 異常なアクセスパターンを検出

3. **証明書の更新**
   - 証明書の有効期限を確認（10年間有効）
   - 必要に応じて再生成

## セキュリティ制限事項

### ⚠️ 開発環境専用

この認証機能は開発・テスト環境向けに設計されています。

**本番環境では以下を実施してください**:

1. **信頼された認証局（CA）の証明書を使用**
2. **HTTPSのみを有効化**（HTTPを無効化）
3. **ファイアウォールで適切なアクセス制限を設定**
4. **監査ログを外部システムに転送**
5. **多要素認証（MFA）の導入を検討**

### 閉域環境での制限

- 外部認証サービス（OAuth, SAML等）は使用不可
- 証明書失効リスト（CRL）の確認不可
- タイムスタンプサーバーへのアクセス不可

## 参考資料

- [ASP.NET Core 認証](https://learn.microsoft.com/ja-jp/aspnet/core/security/authentication/)
- [JWT (JSON Web Token)](https://jwt.io/)
- [BCrypt パスワードハッシュ](https://en.wikipedia.org/wiki/Bcrypt)
- [OpenSSL 証明書生成](https://www.openssl.org/docs/)

---

**最終更新**: 2025年10月8日  
**バージョン**: 1.0.0

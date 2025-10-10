#!/bin/bash

# 自己署名証明書生成スクリプト
# Non-IP File Delivery Web Configuration用

echo "======================================"
echo "自己署名証明書生成スクリプト"
echo "======================================"

# 証明書ディレクトリの作成
CERT_DIR="certificates"
mkdir -p "$CERT_DIR"

# 証明書のパラメータ
CERT_NAME="localhost"
CERT_PATH="$CERT_DIR/$CERT_NAME.pfx"
PEM_CERT="$CERT_DIR/$CERT_NAME.crt"
PEM_KEY="$CERT_DIR/$CERT_NAME.key"
DAYS_VALID=3650  # 10年間有効

# パスワード（空でOK）
PASSWORD=""

echo ""
echo "証明書パラメータ:"
echo "  証明書名: $CERT_NAME"
echo "  有効期限: $DAYS_VALID 日"
echo "  出力先: $CERT_PATH"
echo ""

# OpenSSLで自己署名証明書を生成
echo "OpenSSLで証明書を生成中..."

openssl req -x509 -newkey rsa:4096 -sha256 \
    -days $DAYS_VALID \
    -nodes \
    -keyout "$PEM_KEY" \
    -out "$PEM_CERT" \
    -subj "/CN=localhost/O=NonIP File Delivery/OU=Development" \
    -addext "subjectAltName=DNS:localhost,DNS:*.localhost,IP:127.0.0.1,IP:0.0.0.0"

if [ $? -ne 0 ]; then
    echo "エラー: 証明書の生成に失敗しました"
    exit 1
fi

echo "証明書を生成しました: $PEM_CERT"
echo "秘密鍵を生成しました: $PEM_KEY"

# PFX形式に変換（ASP.NET Coreで使用）
echo ""
echo "PFX形式に変換中..."

openssl pkcs12 -export \
    -out "$CERT_PATH" \
    -inkey "$PEM_KEY" \
    -in "$PEM_CERT" \
    -passout pass:$PASSWORD

if [ $? -ne 0 ]; then
    echo "エラー: PFX変換に失敗しました"
    exit 1
fi

echo "PFX証明書を生成しました: $CERT_PATH"

# 証明書情報の表示
echo ""
echo "======================================"
echo "証明書情報:"
echo "======================================"
openssl x509 -in "$PEM_CERT" -noout -text | grep -A 2 "Subject:"
openssl x509 -in "$PEM_CERT" -noout -text | grep -A 2 "Validity"
openssl x509 -in "$PEM_CERT" -noout -text | grep -A 5 "Subject Alternative Name"

echo ""
echo "======================================"
echo "生成完了"
echo "======================================"
echo ""
echo "以下のファイルが生成されました:"
echo "  - $CERT_PATH (ASP.NET Core用)"
echo "  - $PEM_CERT (証明書)"
echo "  - $PEM_KEY (秘密鍵)"
echo ""
echo "使用方法:"
echo "  1. appsettings.jsonで証明書パスを設定してください"
echo "  2. ブラウザで https://localhost:5001 にアクセス"
echo "  3. 証明書警告が表示されたら「詳細設定」→「続行」"
echo ""
echo "注意:"
echo "  この証明書は開発/テスト環境専用です"
echo "  本番環境では正式なCA署名証明書を使用してください"
echo ""

# Windows用の変換（オプション）
if command -v certutil &> /dev/null; then
    echo "Windows証明書ストアへのインポート方法:"
    echo "  certutil -user -p \"\" -importPFX \"$CERT_PATH\" NoRoot"
    echo ""
fi

echo "完了しました！"

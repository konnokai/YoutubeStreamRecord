# YT錄影小幫手

需配置GoogleApiKey使用

若要搭配直播小幫手使用，需要另外安裝 Redis Server 並使用 Subscribe 模式

## Docker環境，Sub模式

1. 複製專案 `git clone https://github.com/jun112561/Youtube-Stream-Record.git`
2. 開啟 `.env_sample` 編輯為正確設定值後存檔為 `.env` 到專案目錄內
 **\*請務必確定所有路徑皆為絕對路徑\***
3. 部屬 Docker Image `docker compose up -d`

使用 Redis Publish 指令到 youtube.record，參數為11碼 VideoId 即可觸發建立容器並錄影

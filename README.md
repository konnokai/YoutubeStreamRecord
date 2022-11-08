# YT錄影小幫手

需配置GoogleApiKey使用

若要搭配直播小幫手使用，需要另外安裝Redis Server並使用Sub模式

# Docker環境 (還在開發中)
開啟 Dockerfile，編輯 `ENV GoogleApiKey=[GoogleApiKey]` 以及 `ENV RedisOption="127.0.0.1,syncTimeout=3000"` 為正確設定值

1. 建立 Docker Image `docker build -t youtube-record .`
2. 建立 Docker 容器 (請將[]內設定為正確值) `docker create --name youtube-record-sub -v [錄影輸出路徑]:/output -v [錄影暫存路徑]:/temp_path -v [私人直播保存路徑]:/unarchived_stream youtube-record`
3. 啟動 Docker 容器 `docker start youtube-record-sub`
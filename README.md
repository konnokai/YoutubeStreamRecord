# YT錄影小幫手

需配置 Google Api Key 使用

若要搭配直播小幫手使用，需要另外安裝 Redis Server 並使用 Subscribe 模式

## 製作 `cookies.txt`

1. 開啟常用瀏覽器並登入 Youtube (本說明以 Chrome 為例)
2. 下載 `ChromeCookiesView` ([官網](https://www.nirsoft.net/utils/chrome_cookies_view.html), [直接下載](https://www.nirsoft.net/utils/chromecookiesview.zip))
3. 解壓縮並開啟 `ChromeCookiesView.exe`
4. 搜尋 `.youtube.com` 域名相關 Cookie 
5. 將所有搜尋到的 Cookie 複製成 `Netscape Cookie` 格式 (Copy As Cookies.txt Format) 
6. 建立 `cookies.txt` 並將 Cookie 貼上

## 直接執行程式

需要從頭將專案編譯，我相信你可以自己搞定參數設定及如何開始錄影的

## Docker環境，Sub模式

本模式是設計給直播小幫手串接使用，一般無需使用

1. 複製專案 `git clone https://github.com/jun112561/Youtube-Stream-Record.git`
2. 開啟 `.env_sample` 編輯為正確設定值後存檔為 `.env` 到專案目錄內
 **\*請務必確定所有路徑皆為絕對路徑\***
3. 部屬 Docker Image `docker compose up -d`

使用 Redis Publish 指令到 youtube.record，參數為11碼 VideoId 即可觸發建立容器並錄影

## Docker環境，單一直播錄影模式

1. 複製專案 `git clone https://github.com/jun112561/Youtube-Stream-Record.git` (或是單獨下載 `.env_sample` 並放到新資料夾)
2. `cd Youtube-Stream-Record`
3. 根據上方說明製作 `cookies.txt` 並將文件放置專案目錄
4. 開啟 `.env_sample` 並編輯 `GoogleApiKey` 成正確的 ApiKey 後存檔為 `.env` 到專案目錄內

取得11碼的 VideoId 並替換下方指令中的 `(VideoId)` 區塊

執行 `docker run -it -d --env-file .env -v "/record/output:/output" -v "/record/temp:/temp_path" -v "/record/unarchived:/unarchived_stream" -v "/record/member_only:/member_only_stream" -v "/cookies.txt:/app/cookies.txt" jun112561/youtube-record:master onceondocker (VideoId) -d -s`

Docker -v 參數請自行替換成實體主機中要保存的絕對路徑，唯獨 Container 掛載路徑不可變更

若需要從頭開始錄影請將指令最後面的 `-s` 移除

(注意: 從頭開始直播僅可從頭錄影兩小時，無法超過兩小時，尚不確定是 yt-dlp 問題還是 youtube 限制，非特殊情況建議不要從頭開始錄影)

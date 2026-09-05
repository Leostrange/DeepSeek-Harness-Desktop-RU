# DeepSeek Harness Native Client

Настольная оболочка Windows для **DeepSeek Harness** на **WPF + .NET 8** с встроенным **WebView2**.

Окно клиента — это полноценный интерфейс DeepSeek Harness: та же страница `http://127.0.0.1:3080` (workspace, сессии, выбор модели и усилия рассуждений, настройки провайдеров и ключей), только в отдельном окне приложения. Ничего из возможностей веб-интерфейса не теряется и не перерисовывается заново: клиент не дублирует разметку Harness, а показывает её как есть.

## Как это работает

- `DshHost` (в `DshApi.cs`) управляет жизненным циклом фонового процесса Harness: если порт 3080 уже занят — подключается к нему, если нет — запускает `npx --yes @deepseek-ai/dsh web --port 3080` с постоянным `DSH_HOME=%APPDATA%\DeepSeekHarness\data`.
- `MainWindow` — единственное окно с элементом `WebView2`, который загружает `http://127.0.0.1:3080`.
- Требуется **WebView2 Runtime** (есть в Windows 10/11 вместе с Microsoft Edge).

## Сборка

```powershell
dotnet restore
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Готовый файл: `publish\DeepSeekHarness.Native.exe`.

## Примечания

- Настройки, сессии и credentials хранятся в `%APPDATA%\DeepSeekHarness\data` — общие с веб-интерфейсом и лаунчером `DeepSeekHarness.cmd`.
- Ключ API DeepSeek вводится в самом интерфейсе: **Settings → Models**.
- Если порт 3080 уже занят другим процессом, `DshHost` просто откроет его адрес.

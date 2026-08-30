<p align="center"><img src="./assets/leostrange-project-banner.svg" alt="DeepSeek Harness Desktop RU" width="100%" /></p>

<p align="center">
  <a href="https://github.com/Leostrange/DeepSeek-Harness-Desktop-RU/releases/tag/v1.0.0"><img src="https://img.shields.io/badge/Release-v1.0.0-7C3AED?style=flat-square" alt="Release" /></a>
  <img src="https://img.shields.io/badge/Windows-10%2F11-0078D4?style=flat-square&logo=windows&logoColor=white" alt="Windows" />
  <img src="https://img.shields.io/badge/Language-Russian-22D3EE?style=flat-square" alt="Russian UI" />
  <img src="https://img.shields.io/badge/Community-project-64748B?style=flat-square" alt="Community project" />
</p>

<p align="center"><b>Windows-клиент DeepSeek Harness с русской локализацией и автоматической подготовкой окружения.</b></p>

<p align="center"><a href="https://github.com/Leostrange/DeepSeek-Harness-Desktop-RU/releases/tag/v1.0.0"><b>Скачать v1.0.0</b></a> · <a href="#быстрый-старт">Быстрый старт</a> · <a href="#видеодемонстрация">Видео установки</a></p>

<p align="center"><a href="https://github.com/Leostrange/DeepSeek-Harness-Desktop-RU/releases/tag/v1.0.0"><img src="./media/deepseek-harness-chat.jpg" alt="DeepSeek Harness Desktop RU interface" width="900" /></a></p>

---

## О проекте

**DeepSeek Harness Desktop RU** — независимый Windows desktop-дистрибутив для DeepSeek Harness с русской локализацией интерфейса. Установщик подготавливает локальное окружение, запускает Harness и открывает интерфейс на `127.0.0.1:3080`.

Цель проекта — сценарий **«скачал и работай»**: без ручной настройки Node.js, pnpm и отдельного сервера Harness.

> **Независимый community-проект.** Не является официальным продуктом DeepSeek и не аффилирован с DeepSeek.

## Что добавляет RU Desktop-версия

Этот репозиторий использует [официальный DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) как основное программное ядро. Вклад этого проекта — **Windows-оболочка, установщик, desktop-упаковка, автоматическая подготовка окружения и русская локализация**.

Если нужна оригинальная кодовая база Harness, документация и актуальная разработка, используйте [официальный репозиторий DeepSeek AI](https://github.com/deepseek-ai/deepseek-harness).

## Возможности

| Возможность | Что это даёт |
|---|---|
| **Локальный запуск** | Harness работает на `127.0.0.1:3080`. |
| **Русская локализация** | Основные элементы интерфейса переведены на русский язык. |
| **Автоподготовка среды** | Установщик проверяет Node.js и при необходимости устанавливает его. |
| **Windows installer** | Рекомендуемая установка через `DeepSeekHarness-Setup.exe`. |
| **Portable-сборка** | Запуск из распакованной папки без классической установки. |
| **Native-пакет** | Отдельная native-сборка для соответствующего сценария. |
| **Проверка подписи** | В релиз входит `DeepSeekHarness-CodeSigning.cer`. |
| **Оригинальный Harness** | Сохраняются сессии, модели, плагины, пресеты и настройки. |

## Быстрый старт

1. Откройте [релиз v1.0.0](https://github.com/Leostrange/DeepSeek-Harness-Desktop-RU/releases/tag/v1.0.0).
2. Скачайте `DeepSeekHarness-Setup.exe`.
3. Запустите установщик и пройдите мастер установки.
4. Клиент подготовит окружение, запустит локальный Harness и подключится к `http://127.0.0.1:3080`.

> Если Node.js отсутствует, установщик обнаружит это и автоматически установит необходимую среду.

## Файлы релиза

| Файл | Назначение |
|---|---|
| [`DeepSeekHarness-Setup.exe`](https://github.com/Leostrange/DeepSeek-Harness-Desktop-RU/releases/download/v1.0.0/DeepSeekHarness-Setup.exe) | Рекомендуемый установщик. |
| [`DeepSeekHarness-Distribution.zip`](https://github.com/Leostrange/DeepSeek-Harness-Desktop-RU/releases/download/v1.0.0/DeepSeekHarness-Distribution.zip) | Distribution / portable-сборка. |
| [`DeepSeekHarness-Native.zip`](https://github.com/Leostrange/DeepSeek-Harness-Desktop-RU/releases/download/v1.0.0/DeepSeekHarness-Native.zip) | Native-сборка. |
| [`DeepSeekHarness-CodeSigning.cer`](https://github.com/Leostrange/DeepSeek-Harness-Desktop-RU/releases/download/v1.0.0/DeepSeekHarness-CodeSigning.cer) | Сертификат для проверки подписи. |

## Интерфейс

<p align="center"><img src="./media/deepseek-harness-settings.jpg" alt="DeepSeek Harness Desktop RU settings" width="820" /><br/><sub>Язык, права, модели, плагины и оформление внутри desktop-оболочки.</sub></p>

## Видеодемонстрация

Установка показана в **чистой Windows-песочнице**, где Node.js заранее отсутствует: запуск инсталлятора → обнаружение зависимости → установка Node.js → запуск локального Harness.

<p align="center"><a href="https://github.com/Leostrange/DeepSeek-Harness-Desktop-RU/blob/main/media/deepseek-harness-install-demo.mp4"><img src="./media/deepseek-harness-chat.jpg" alt="Open install demo" width="760" /><br/><b>▶ Открыть видеодемонстрацию установки</b></a></p>

## SHA-256

```text
ee93e434437ad301f9cb23c0f4080b952b578c3e3172a0b2b77cfd53c3a6d17d  DeepSeekHarness-Setup.exe
e9f3b7c0b7cb9225601227ea8725e79b72851befe692248566e8ff9e88591b2e  DeepSeekHarness-Distribution.zip
f454bae87e09790f7a444d3e5066c284d660247e527485c5dfa3e2e19da0efa4  DeepSeekHarness-Native.zip
73468508aba59723b2d8e054dc75e70b7c6ccef762b9a278832a955bd8705a19  DeepSeekHarness-CodeSigning.cer
```

Перед использованием в рабочей среде рекомендуется проверить подпись, SHA-256 и совместимость с целевой версией Windows.

---

## English

**DeepSeek Harness Desktop RU** is an independent Windows desktop distribution for DeepSeek Harness with built-in Russian UI localization.

The installer prepares the local runtime automatically. If Node.js is missing, it installs the required environment and launches local Harness at `127.0.0.1:3080`.

**Highlights:** Windows desktop experience · automatic local connection · Russian UI · automatic Node.js setup · installer + portable/native packages · code-signing certificate.

> Independent community project. Not affiliated with or endorsed by DeepSeek.

<p align="center"><a href="https://github.com/Leostrange/DeepSeek-Harness-Desktop-RU/releases/tag/v1.0.0"><b>Download DeepSeek Harness Desktop RU v1.0.0</b></a></p>

---

<p align="center"><sub>Part of the <a href="https://github.com/Leostrange">Leostrange</a> open-source projects.</sub></p>

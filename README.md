# DeepSeek Harness Desktop RU

> DeepSeek Harness as a Windows desktop application — with Russian UI out of the box.

[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4)](https://github.com/Leostrange/DeepSeek-Harness-Desktop-RU)
[![DeepSeek Harness](https://img.shields.io/badge/DeepSeek-Harness-4D6BFE)](https://github.com/deepseek-ai/deepseek-harness)
[![Language](https://img.shields.io/badge/UI-Русский-2ea44f)](#русская-локализация)
[![dsh-plugin](https://img.shields.io/badge/topic-dsh--plugin-black)](https://github.com/topics/dsh-plugin)

**Нативное окно Windows · автоматическое подключение к локальному Harness · русский интерфейс · компактный установщик**

---

## О проекте

Официальный **DeepSeek Harness** по умолчанию открывает Web UI в браузере. **DeepSeek Harness Desktop RU** превращает его в привычное Windows-приложение: запускает/подключается к локальному Harness на `http://127.0.0.1:3080`, показывает оригинальный интерфейс DSH в отдельном desktop-окне и добавляет полноценную русскую локализацию.

Это не отдельный DeepSeek-чат и не замена Harness. В приложении остаются штатные рабочие области, сессии, модели, плагины, пресеты агентов, разрешения и настройки DeepSeek Harness.

> [!NOTE]
> Независимый community-проект. Не является официальным продуктом DeepSeek.

## Возможности

| | Возможность |
|---|---|
| 🪟 | DeepSeek Harness в отдельном Windows-окне вместо вкладки браузера |
| 🔌 | Автоматическое подключение к локальному `127.0.0.1:3080` |
| 🇷🇺 | Полноценный русский интерфейс непосредственно внутри Harness |
| 🌐 | `中文 / English / Русский` в штатном переключателе языка |
| 📦 | Компактный Windows installer и готовая desktop-сборка |
| 🧩 | Все основные возможности оригинального Harness остаются доступны |
| 💾 | Локальная работа Harness и сохранение пользовательских данных |

## Скриншоты

### Desktop-клиент

![DeepSeek Harness Desktop RU](docs/media/harness-desktop-ru.jpg)

### Русский язык в настройках

![Русский язык DeepSeek Harness](docs/media/harness-language-ru.jpg)

Русский добавлен именно в интерфейс Harness, а не только в оболочку Windows-клиента.

## Видео установки

Полная демонстрация установки и первого запуска будет доступна в `docs/media/installation-demo.mp4`.

## Установка

1. Скачайте `DeepSeekHarness-Setup.exe` из **Releases**.
2. Запустите установщик.
3. После установки откройте **DeepSeek Harness** как обычное Windows-приложение.
4. Клиент подключится к локальному экземпляру Harness на `127.0.0.1:3080`.
5. Откройте **Настройки → Модели** и настройте нужного провайдера/API-ключ.
6. Русский язык доступен в **Настройки → Язык → Русский**.

> Готовые крупные бинарные файлы распространяются через GitHub Releases, а не хранятся в Git history.

## Русская локализация

Локализация охватывает основной интерфейс DeepSeek Harness: навигацию, настройки, модели, плагины, пресеты агентов, разрешения, элементы сессий и другие пользовательские строки.

Главное отличие проекта — русский является полноценным вариантом языка внутри самого Harness:

```text
中文   English   Русский
```

## Как это работает

```text
┌──────────────────────────────┐
│ DeepSeek Harness Desktop RU  │
│       Windows Client         │
└──────────────┬───────────────┘
               │
               ▼
┌──────────────────────────────┐
│      Local Harness UI        │
│   http://127.0.0.1:3080      │
└──────────────┬───────────────┘
               │
               ▼
┌──────────────────────────────┐
│      DeepSeek Harness        │
│   + Russian localization     │
└──────────────────────────────┘
```

Desktop-клиент не пытается заново реализовать функции Harness. Он предоставляет отдельное Windows-окно для локального Web UI и автоматизирует обычный browser-based сценарий запуска.

## Требования

- Windows 10/11 x64;
- локальный DeepSeek Harness/runtime, поставляемый соответствующей сборкой;
- интернет требуется для API-провайдеров и операций, которым он нужен самому Harness.

## Безопасность

Проект не должен содержать пользовательские API-ключи. Credentials настраиваются через штатный интерфейс DeepSeek Harness. Не публикуйте каталоги пользовательских данных или конфигурации, содержащие секреты.

## Upstream

Официальный проект: [deepseek-ai/deepseek-harness](https://github.com/deepseek-ai/deepseek-harness)

Экосистема плагинов: [GitHub topic: dsh-plugin](https://github.com/topics/dsh-plugin)

DeepSeek, DeepSeek Harness и связанные названия принадлежат их соответствующим владельцам. Этот проект не заявляет официальной аффилиации с DeepSeek.

---

## English

**DeepSeek Harness Desktop RU** is an independent Windows desktop distribution for the official DeepSeek Harness with built-in Russian UI localization.

Instead of using Harness in a regular browser tab, the client connects to the local Harness Web UI at `127.0.0.1:3080` and presents it as a standalone Windows application. Russian is integrated directly into the Harness language selector alongside Chinese and English.

### Highlights

- standalone Windows experience;
- automatic connection to local DeepSeek Harness;
- original Harness UI and functionality;
- built-in Russian localization;
- compact Windows installer;
- no need to manually open the Harness URL in a browser.

### Disclaimer

This is an independent community project and is not an official DeepSeek product.

---

<div align="center">

**DeepSeek Harness · Windows · Русский**

</div>

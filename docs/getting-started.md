# Быстрый старт

## Требования

- Доступ к серверу Pilot.
- .NET 8 Runtime, если выбран архив без встроенного .NET (для `.mcpb` и `selfcontained.zip` не нужен).

## Установка

Готовые сборки (Windows x64) лежат на странице [Releases](https://github.com/mahach666/PilotMCP/releases):

| Файл | Для чего |
| ---- | -------- |
| `PilotMCP-vX.Y.Z-win-x64.mcpb` | MCP Bundle: установка в Claude Desktop в один клик, параметры подключения вводятся в форме |
| `PilotMCP-vX.Y.Z-win-x64-selfcontained.zip` | Распаковать и указать `PilotMCP.exe` в конфигурации клиента; .NET не нужен |
| `PilotMCP-vX.Y.Z-win-x64.zip` | То же, но нужен установленный .NET 8 Runtime |

### Claude Desktop через .mcpb

1. Скачайте `PilotMCP-vX.Y.Z-win-x64.mcpb` со страницы Releases.
2. Откройте файл в Claude Desktop (двойной щелчок или перетаскивание в окно).
3. В форме укажите URL сервера, логин и пароль. Остальные поля необязательны: база данных, код лицензии, режим только чтения, учётная запись администратора сервера. Описание полей — в разделе [Конфигурация](configuration.md).

### Через zip-архив

1. Распакуйте архив в постоянную папку.
2. Укажите путь к `PilotMCP.exe` в конфигурации MCP-клиента, а параметры подключения передайте переменными окружения (примеры ниже).

### MCP Registry

Сервер опубликован в официальном [MCP Registry](https://registry.modelcontextprotocol.io) под именем `io.github.mahach666/pilotmcp`.

## Подключение к MCP-клиенту

Во всех примерах путь `C:\\path\\to\\PilotMCP.exe` замените на реальный, а значения `PILOT_*` — на свои. Обязательны `PILOT_SERVER_URL`, `PILOT_LOGIN` и `PILOT_PASSWORD`; остальные переменные описаны в разделе [Конфигурация](configuration.md).

### Claude Desktop

Файл `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "pilot": {
      "command": "C:\\path\\to\\PilotMCP.exe",
      "env": {
        "PILOT_SERVER_URL": "http://localhost:5545/u_db_name",
        "PILOT_LOGIN": "username",
        "PILOT_PASSWORD": "password"
      }
    }
  }
}
```

### Claude Code

Через командную строку:

```bash
claude mcp add pilot --env PILOT_SERVER_URL=http://localhost:5545/u_db_name --env PILOT_LOGIN=username --env PILOT_PASSWORD=password -- "C:\path\to\PilotMCP.exe"
```

Либо файл `.mcp.json` в корне проекта с тем же содержимым, что и для Claude Desktop.

### Cursor

Файл `.cursor/mcp.json` в проекте или `~/.cursor/mcp.json` глобально:

```json
{
  "mcpServers": {
    "pilot": {
      "command": "C:\\path\\to\\PilotMCP.exe",
      "env": {
        "PILOT_SERVER_URL": "http://localhost:5545/u_db_name",
        "PILOT_LOGIN": "username",
        "PILOT_PASSWORD": "password"
      }
    }
  }
}
```

### VS Code

Файл `.vscode/mcp.json` (в VS Code корневой ключ называется `servers`):

```json
{
  "servers": {
    "pilot": {
      "type": "stdio",
      "command": "C:\\path\\to\\PilotMCP.exe",
      "env": {
        "PILOT_SERVER_URL": "http://localhost:5545/u_db_name",
        "PILOT_LOGIN": "username",
        "PILOT_PASSWORD": "password"
      }
    }
  }
}
```

### Прочие клиенты

LM Studio, Ollama-клиенты и другие используют формат `mcpServers`, как в примере для Claude Desktop.

## Проверка

После запуска сервер при наличии трёх обязательных переменных сам подключается к Pilot и пишет результат в stderr (строка вида `[PilotMCP] Подключено к ...`). Если автоподключение не удалось, причина также пишется в stderr, а инструменты возвращают сообщение «Не подключено к серверу Pilot» с последними ошибками. Простейшая проверка из клиента — вызвать инструмент [`GetDatabaseInfo`](tools/connection.md).

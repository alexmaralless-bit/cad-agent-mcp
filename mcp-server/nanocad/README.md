# MCP-сервер для nanoCAD 24.1

Node.js/TypeScript-прокси: говорит с агентом по протоколу MCP через stdio и пересылает вызовы в HTTP-мост плагина `ClaudeBridge.NanoCad` на `http://127.0.0.1:7890`. Инструментов: 15.

## Требования

- Node.js 18+
- nanoCAD 24.1 с загруженным плагином `ClaudeBridge.NanoCad` (проверка: `curl http://127.0.0.1:7890/healthz`)

## Сборка

```powershell
npm install
npm run build
```

## Подключение

```powershell
claude mcp add nano-bridge -- node "<путь-к-репозиторию>\mcp-server\nanocad\dist\index.js"
```

## Переменные окружения

- `BRIDGE_URL` — адрес HTTP-моста, по умолчанию `http://127.0.0.1:7890`
- `BRIDGE_TIMEOUT_MS` — таймаут запроса, по умолчанию `35000` (сам мост обрывает запрос через 30 с)

## Инструменты

| Инструмент | Что делает |
|---|---|
| `healthz` | проверка связи с плагином |
| `get_document_summary` | слои, блоки, счётчики объектов, габариты |
| `get_view` | PNG-снимок окна чертежа + метаданные |
| `query_objects` | поиск объектов по фильтрам (тип, слой, блок, рамка) |
| `probe` | диагностический дамп API |
| `camera_zoom_extents`, `camera_zoom_window` | управление видом |
| `overlay_highlight`, `overlay_hud`, `overlay_grid`, `overlay_scale_bar`, `overlay_markers`, `overlay_clear` | пометки на служебном слое `CLAUDE_OVERLAY`, стираются после снимка |
| `execute_command`, `run_lisp` | запись: dry-run → подтверждение → резервная копия .dwg → выполнение |

## Smoke-скрипты

`scripts/smoke*.mjs` — ручные проверки против запущенного моста: `node scripts/smoke.mjs` после `npm run build`.
